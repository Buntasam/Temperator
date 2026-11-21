using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

namespace Temperator;

/// <summary>Les valeurs affichées par le widget (copie : l'interface ne touche pas aux objets du thread capteurs).</summary>
public sealed class Readings
{
    public bool Ready;
    public string? CpuName, GpuName, DiskName;
    public float? CpuTemp, CpuLoad, CpuPower;
    public float? GpuTemp, GpuLoad;
    public float? RamLoad, RamUsed, RamTotal;
    public float? DiskTemp, DiskLoad;
    public float? FanRpm;
    public float? GpuPower;
    /// <summary>Consommation CPU + GPU, en watts (approximative : le reste du PC n'est pas mesuré).</summary>
    public float? TotalPower;
    /// <summary>Réseau, en octets par seconde.</summary>
    public float? NetDown, NetUp;
    public string? NetName;
    /// <summary>Batterie : niveau en %, en charge ou non, débit en watts (+ charge, − décharge).</summary>
    public float? BatteryLevel, BatteryRate;
    public bool BatteryCharging;
    /// <summary>Le matériel au choix (identifiant, nom) : cartes graphiques, disques, cartes réseau.</summary>
    public List<(string Id, string Name)> Gpus = new(), Disks = new(), Nets = new();
    /// <summary>Chaque ventilateur à part (carte mère, carte graphique, refroidissement).</summary>
    public List<FanInfo> Fans = new();
    /// <summary>Sécurité : un composant est trop chaud, les ventilateurs réglés à la main tournent à fond.</summary>
    public bool FanBoost;
}

/// <summary>Un ventilateur : sa vitesse et, s'il se règle depuis Windows, son réglage.</summary>
/// <param name="Key">identifiant stable (celui du réglage, sinon celui du capteur de vitesse)</param>
/// <param name="Device">le matériel qui le porte (puce de la carte mère, carte graphique…)</param>
/// <param name="Duty">vitesse demandée en ce moment, en %</param>
/// <param name="ControlId">identifiant du réglage ; null : vitesse seulement lue</param>
/// <param name="Manual">vitesse imposée par un logiciel (et non par le BIOS ou le pilote)</param>
public sealed record FanInfo(string Key, string Name, string Device, float? Rpm, float? Duty, string? ControlId, bool Manual);

/// <summary>Lit les capteurs (LibreHardwareMonitor) sur un thread à part et publie les valeurs sur le thread de l'interface.</summary>
public sealed class SensorHub : IDisposable
{
    readonly Computer computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        IsControllerEnabled = true,
        IsNetworkEnabled = true,
        IsBatteryEnabled = true,
    };
    readonly Dispatcher dispatcher;
    readonly Thread thread;
    readonly AutoResetEvent wake = new(false), fanWake = new(false);
    volatile bool stopping;
    bool loggedError;

    /// <summary>À verrouiller pour parcourir le matériel depuis l'interface (le thread capteurs le modifie).</summary>
    public readonly object Sync = new();
    public int IntervalMs { get; set; } = 1000;
    /// <summary>Matériel choisi dans les réglages (null : automatique).</summary>
    public volatile string? GpuId, DiskId, NetId;
    public bool Ready { get; private set; }
    /// <summary>Seuils « trop chaud » (°C) : au-delà, les ventilateurs réglés à la main repassent à fond.</summary>
    public volatile float CpuHot = 85, GpuHot = 87;
    public event Action<Readings>? Updated;

    public SensorHub(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        thread = new Thread(Loop) { IsBackground = true, Name = "Capteurs", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>Matériel détecté, sans les cartes réseau virtuelles (appeler sous lock(Sync)).</summary>
    public IReadOnlyList<IHardware> Hardware => Ready ? computer.Hardware.Where(h => !Ignored(h)).ToList() : Array.Empty<IHardware>();

    void Loop()
    {
        try
        {
            lock (Sync)
            {
                computer.Open();
                LightenNvidia(computer.Hardware);
            }
            Ready = true;
        }
        catch (Exception ex) { App.Log(ex); }

        while (!stopping)
        {
            var r = new Readings();
            lock (Sync)
            {
                try
                {
                    if (Ready)
                    {
                        var hw = computer.Hardware.ToList();
                        foreach (var h in hw)
                            if (!Ignored(h)) Update(h);
                        r = Read(hw, GpuId, DiskId, NetId, NvidiaWatts);
                        Fans(hw, r);
                    }
                }
                catch (Exception ex)
                {
                    if (!loggedError) { App.Log(ex); loggedError = true; }
                }
            }
            RamFallback(r);
            BatteryFallback(r);
            if (stopping) break;
            dispatcher.InvokeAsync(() => Updated?.Invoke(r));
            Wait(Math.Clamp(IntervalMs, 250, 10000));
        }

        lock (Sync)
        {
            RestoreFans();
            try { computer.Close(); } catch { }
        }
    }

    /// <summary>Attend la prochaine mesure ; un réglage de ventilateur demandé entre-temps s'applique tout de suite.</summary>
    void Wait(int ms)
    {
        long until = Environment.TickCount64 + ms;
        var handles = new WaitHandle[] { wake, fanWake };
        while (!stopping)
        {
            long left = until - Environment.TickCount64;
            if (left <= 0) return;
            if (WaitHandle.WaitAny(handles, (int)left) != 1) return;
            lock (Sync)
            {
                try { if (Ready) ApplyFans(all: false); }
                catch (Exception ex) { App.Log(ex); }
            }
        }
    }

    static void Update(IHardware h)
    {
        h.Update();
        foreach (var sub in h.SubHardware) Update(sub);
    }

    // ------------------------------------------------------------------ choix des capteurs à afficher

    /// <param name="gpuWatts">consommation lue à part (cartes NVIDIA, voir <see cref="LightenNvidia"/>)</param>
    static Readings Read(List<IHardware> hw, string? gpuId, string? diskId, string? netId, Func<IHardware, float?>? gpuWatts = null)
    {
        var r = new Readings { Ready = true };

        var cpu = hw.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu != null)
        {
            r.CpuName = cpu.Name;
            r.CpuTemp = Find(cpu, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)", "CPU Cores", "Core Average", "Core Max")
                        ?? Max(cpu, SensorType.Temperature);
            r.CpuLoad = Find(cpu, SensorType.Load, "CPU Total");
            r.CpuPower = Find(cpu, SensorType.Power, "CPU Package", "Package");
        }

        // carte graphique : celle choisie, sinon la dédiée en priorité, sinon la puce intégrée
        var gpus = hw.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel).ToList();
        r.Gpus = gpus.Select(h => (h.Identifier.ToString(), h.Name)).ToList();
        var gpu = gpus.FirstOrDefault(h => h.Identifier.ToString() == gpuId)
                  ?? gpus.FirstOrDefault(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
                  ?? gpus.FirstOrDefault();
        if (gpu != null)
        {
            r.GpuName = gpu.Name;
            r.GpuTemp = Find(gpu, SensorType.Temperature, "GPU Core", "GPU Hot Spot") ?? Max(gpu, SensorType.Temperature);
            r.GpuLoad = Find(gpu, SensorType.Load, "GPU Core", "D3D 3D") ?? Max(gpu, SensorType.Load);
            r.GpuPower = Find(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Board Power", "GPU Core") ?? Max(gpu, SensorType.Power) ?? gpuWatts?.Invoke(gpu);
        }
        if (r.CpuPower.HasValue || r.GpuPower.HasValue) r.TotalPower = (r.CpuPower ?? 0) + (r.GpuPower ?? 0);

        var mem = hw.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && !h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        if (mem != null)
        {
            r.RamLoad = Find(mem, SensorType.Load, "Memory");
            r.RamUsed = Find(mem, SensorType.Data, "Memory Used", "Used Memory");
            var avail = Find(mem, SensorType.Data, "Memory Available", "Available Memory");
            if (r.RamUsed.HasValue && avail.HasValue) r.RamTotal = r.RamUsed + avail;
        }

        // disques : celui choisi, sinon le plus chaud (et l'activité la plus forte)
        var disks = hw.Where(h => h.HardwareType == HardwareType.Storage).ToList();
        r.Disks = disks.Select(h => (h.Identifier.ToString(), h.Name)).ToList();
        var chosen = disks.FirstOrDefault(h => h.Identifier.ToString() == diskId);
        foreach (var d in chosen != null ? new List<IHardware> { chosen } : disks)
        {
            var t = Find(d, SensorType.Temperature, "Temperature", "Composite Temperature") ?? Max(d, SensorType.Temperature);
            if (t.HasValue && (r.DiskTemp == null || t > r.DiskTemp)) { r.DiskTemp = t; r.DiskName = d.Name; }
            var a = Find(d, SensorType.Load, "Total Activity");
            if (a.HasValue && (r.DiskLoad == null || a > r.DiskLoad)) r.DiskLoad = a;
            if (chosen != null) r.DiskName = d.Name;
        }

        // réseau : la carte choisie, sinon la plus active en ce moment
        // cartes réseau : sans les cartes virtuelles de Windows (Hyper-V, WAN Miniport, filtres…), une seule par nom
        var real = RealAdapters();
        var nets = hw.Where(h => h.HardwareType.ToString() == "Network" && real.Contains(h.Name))
                     .GroupBy(h => h.Name).Select(g => g.First()).ToList();
        r.Nets = nets.Select(h => (h.Identifier.ToString(), h.Name)).ToList();
        IHardware? net = nets.FirstOrDefault(h => h.Identifier.ToString() == netId);
        if (net == null)
        {
            float best = -1;
            foreach (var n in nets)
            {
                float total = (Find(n, SensorType.Throughput, "Download Speed") ?? 0) + (Find(n, SensorType.Throughput, "Upload Speed") ?? 0);
                if (total > best) { best = total; net = n; }
            }
        }
        if (net != null)
        {
            r.NetName = net.Name;
            r.NetDown = Find(net, SensorType.Throughput, "Download Speed");
            r.NetUp = Find(net, SensorType.Throughput, "Upload Speed");
        }

        // batterie (portables) : débit de charge ou de décharge
        var battery = hw.FirstOrDefault(h => h.HardwareType.ToString() == "Battery");
        if (battery != null)
        {
            r.BatteryLevel = Find(battery, SensorType.Level, "Charge Level");
            var charge = Find(battery, SensorType.Power, "Charge Rate");
            var discharge = Find(battery, SensorType.Power, "Discharge Rate");
            if (charge > 0) { r.BatteryRate = charge; r.BatteryCharging = true; }
            else if (discharge > 0) r.BatteryRate = -discharge;
        }

        // ventilateur : le plus rapide de la carte mère / du refroidissement, sinon celui de la carte graphique
        var fans = hw.Where(h => h.HardwareType == HardwareType.Motherboard || h.HardwareType.ToString() == "Cooler")
                     .SelectMany(AllSensors)
                     .Where(s => s.SensorType == SensorType.Fan && Valid(s) && s.Value > 0)
                     .Select(s => s.Value!.Value)
                     .ToList();
        r.FanRpm = fans.Count > 0 ? fans.Max() : gpu != null ? Max(gpu, SensorType.Fan) : null;
        if (r.FanRpm <= 0) r.FanRpm = null;

        return r;
    }

    static readonly string[] VirtualAdapters =
        { "virtual", "pseudo", "loopback", "miniport", "filter", "hyper-v", "vethernet", "npcap", "kernel debug", "teredo", "isatap", "6to4", "vmware", "virtualbox", "tap-", "wintun", "bluetooth",
          "qos", "packet scheduler" };

    /// <summary>
    /// Vraie carte réseau (Wi-Fi, Ethernet…) d'après son nom et sa description Windows. Exclut les cartes virtuelles
    /// et les filtres que Windows attache à chaque carte (« Ethernet-QoS Packet Scheduler-0000 »…).
    /// </summary>
    internal static bool IsRealAdapter(string name, string description) =>
        !VirtualAdapters.Any(v => description.Contains(v, StringComparison.OrdinalIgnoreCase) || name.Contains(v, StringComparison.OrdinalIgnoreCase))
        && !System.Text.RegularExpressions.Regex.IsMatch(name, @"-\d{4}$");

    static HashSet<string> realAdapters = new();
    static DateTime realAdaptersAt = DateTime.MinValue;
    /// <summary>Windows signale un changement de réseau (carte branchée, Wi-Fi activé…) : la liste est à refaire.</summary>
    static volatile bool adaptersChanged = true;

    static SensorHub()
    {
        try
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => adaptersChanged = true;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) => adaptersChanged = true;
        }
        catch { }
    }

    /// <summary>
    /// Noms des vraies cartes réseau (Wi-Fi, Ethernet…), d'après Windows : la description dit si une carte est virtuelle
    /// (« Ethernet 2 » peut être une carte VirtualBox). Demander la liste à Windows coûte cher : on ne le fait que quand
    /// le réseau change, et par sécurité toutes les 10 minutes.
    /// </summary>
    static HashSet<string> RealAdapters()
    {
        if (!adaptersChanged && DateTime.UtcNow - realAdaptersAt < TimeSpan.FromMinutes(10)) return realAdapters;
        adaptersChanged = false;
        realAdaptersAt = DateTime.UtcNow;
        try
        {
            realAdapters = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not (System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                            && IsRealAdapter(n.Name, n.Description))
                .Select(n => n.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return realAdapters;
    }

    /// <summary>
    /// Carte réseau virtuelle (filtres de Windows, VirtualBox, Hyper-V…) : Windows en déclare des dizaines par vraie carte.
    /// Temperator ne les affiche pas, donc ne les lit pas.
    /// </summary>
    static bool Ignored(IHardware h) => h.HardwareType.ToString() == "Network" && !RealAdapters().Contains(h.Name);

    static IEnumerable<ISensor> AllSensors(IHardware h)
    {
        foreach (var s in h.Sensors) yield return s;
        foreach (var sub in h.SubHardware)
            foreach (var s in AllSensors(sub)) yield return s;
    }

    static bool Valid(ISensor s) =>
        s.Value is float v && !float.IsNaN(v) && !float.IsInfinity(v) &&
        (s.SensorType != SensorType.Temperature || (v > 0 && v < 150 && !IsDelta(s)));

    /// <summary>« Distance to TjMax » : un écart, pas une température.</summary>
    public static bool IsDelta(ISensor s) => s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase);

    static float? Find(IHardware h, SensorType type, params string[] names)
    {
        var list = AllSensors(h).Where(s => s.SensorType == type && Valid(s)).ToList();
        foreach (var name in names)
        {
            var s = list.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s.Value;
        }
        return null;
    }

    static float? Max(IHardware h, SensorType type)
    {
        var values = AllSensors(h).Where(s => s.SensorType == type && Valid(s)).Select(s => s.Value!.Value).ToList();
        return values.Count > 0 ? values.Max() : null;
    }

    // ------------------------------------------------------------------ NVIDIA : sans les deux mesures qui coûtent cher

    const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    static readonly Type? Nvml = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Interop.NvidiaML");
    static readonly System.Reflection.MethodInfo? NvmlPowerUsage = Nvml?.GetMethod("NvmlDeviceGetPowerUsage",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
    /// <summary>Accès NVML de chaque carte NVIDIA, retiré à LibreHardwareMonitor (thread capteurs).</summary>
    readonly Dictionary<IHardware, object> nvidia = new();

    /// <summary>
    /// Une carte NVIDIA coûtait à elle seule ~10 % d'un cœur, à cause de deux valeurs que Temperator n'affiche pas :
    /// <list type="bullet">
    /// <item>le débit PCIe (NVML) : chaque mesure occupe le processeur 20 ms, deux fois par lecture. On retire l'accès
    /// NVML à LibreHardwareMonitor et on lit nous-mêmes la seule valeur NVML utile, la consommation en watts ;</item>
    /// <item>la consommation en % de l'enveloppe (NVAPI « power topology ») : ~15 ms par lecture. On retire ces capteurs,
    /// et LibreHardwareMonitor ne les interroge plus.</item>
    /// </list>
    /// Températures, utilisation, fréquences, mémoire et ventilateurs restent lus normalement. Si LibreHardwareMonitor
    /// change (champ ou méthode introuvable), la partie concernée n'est pas retirée : tout fonctionne comme avant.
    /// </summary>
    void LightenNvidia(IEnumerable<IHardware> hardware)
    {
        foreach (var h in hardware.Where(h => h.HardwareType == HardwareType.GpuNvidia))
        {
            try
            {
                var field = h.GetType().GetField("_nvmlDevice", Private);
                if (NvmlPowerUsage != null && field?.GetValue(h) is { } device && device.GetType() == NvmlPowerUsage.GetParameters()[0].ParameterType)
                {
                    field.SetValue(h, null);
                    nvidia[h] = device;
                }
                // occupation de chaque moteur et mémoire vues par Direct3D : NVAPI donne déjà l'utilisation (« GPU Core »)
                h.GetType().GetField("_d3dDeviceId", Private)?.SetValue(h, null);
            }
            catch (Exception ex) { App.Log(ex); }

            try
            {
                var powers = h.GetType().GetField("_powers", Private);
                var deactivate = h.GetType().GetMethod("DeactivateSensor", Private);
                if (powers?.GetValue(h) is ISensor?[] sensors && deactivate != null)
                {
                    foreach (var s in sensors) if (s != null) deactivate.Invoke(h, new object[] { s });
                    powers.SetValue(h, null);
                }
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    /// <summary>Consommation d'une carte NVIDIA par NVML (un appel immédiat), ou null.</summary>
    float? NvidiaWatts(IHardware gpu)
    {
        if (!nvidia.TryGetValue(gpu, out var device)) return null;
        try { return NvmlPowerUsage!.Invoke(null, new[] { device }) is int milliwatts && milliwatts > 0 ? milliwatts / 1000f : null; }
        catch { return null; }
    }

    // ------------------------------------------------------------------ ventilateurs : chacun à part, et leur réglage

    readonly object fanLock = new();
    /// <summary>Vitesse voulue par réglage (identifiant → %) ; absent : automatique. Sous lock(fanLock).</summary>
    readonly Dictionary<string, float> fanTargets = new();
    /// <summary>Réglages à (ré)appliquer. Sous lock(fanLock).</summary>
    readonly HashSet<string> fanPending = new();
    /// <summary>Réglages imposés par Temperator (rendus au BIOS / au pilote en quittant). Thread capteurs.</summary>
    readonly HashSet<string> fanTouched = new();
    /// <summary>Ventilateurs déjà vus tourner (un ventilateur qui s'arrête reste affiché). Thread capteurs.</summary>
    readonly HashSet<string> fanSeen = new();
    bool boost, loggedFanError;

    /// <summary>Impose une vitesse (en %) à un ventilateur, ou le rend au BIOS / au pilote (null).</summary>
    public void SetFan(string controlId, float? percent)
    {
        lock (fanLock)
        {
            if (percent is float p) fanTargets[controlId] = Math.Clamp(p, (float)AppSettings.MinFanSpeed, 100);
            else fanTargets.Remove(controlId);
            fanPending.Add(controlId);
        }
        fanWake.Set();
    }

    /// <summary>Réglages en attente, sécurité, puis lecture de chaque ventilateur (sous lock(Sync)).</summary>
    void Fans(List<IHardware> hw, Readings r)
    {
        try
        {
            ApplyFans(all: false);
            Safety(r);
            r.Fans = ReadFans(hw);
        }
        catch (Exception ex)
        {
            if (!loggedFanError) { App.Log(ex); loggedFanError = true; }
        }
    }

    /// <summary>Les réglages des ventilateurs, par identifiant (sous lock(Sync)).</summary>
    Dictionary<string, ISensor> Controls() =>
        computer.Hardware.SelectMany(AllSensors)
            .Where(s => s.SensorType == SensorType.Control && s.Control != null)
            .GroupBy(s => s.Identifier.ToString())
            .ToDictionary(g => g.Key, g => g.First());

    /// <summary>Applique les vitesses demandées (sous lock(Sync)) : seulement les nouvelles, ou toutes.</summary>
    void ApplyFans(bool all)
    {
        List<(string Id, float? Target)> work;
        lock (fanLock)
        {
            var ids = all ? fanTargets.Keys.Concat(fanPending).Distinct().ToList() : fanPending.ToList();
            fanPending.Clear();
            work = ids.Select(id => (id, fanTargets.TryGetValue(id, out var p) ? p : (float?)null)).ToList();
        }
        if (work.Count == 0) return;

        var controls = Controls();
        foreach (var (id, target) in work)
        {
            if (!controls.TryGetValue(id, out var sensor) || sensor.Control is not { } control) continue;
            try
            {
                if (target is float p)
                {
                    float min = Math.Max(control.MinSoftwareValue, (float)AppSettings.MinFanSpeed);
                    float max = control.MaxSoftwareValue > min ? control.MaxSoftwareValue : 100;
                    control.SetSoftware(boost ? max : Math.Clamp(p, min, max));
                    fanTouched.Add(id);
                }
                else
                {
                    control.SetDefault();
                    fanTouched.Remove(id);
                }
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    /// <summary>
    /// Sécurité : si le CPU ou la carte graphique dépasse son seuil « trop chaud », les ventilateurs réglés à la main
    /// tournent à fond, jusqu'à ce que tout soit redescendu de 5 °C.
    /// </summary>
    void Safety(Readings r)
    {
        float cpu = CpuHot, gpu = GpuHot;
        bool hot = r.CpuTemp >= cpu || r.GpuTemp >= gpu;
        bool warm = r.CpuTemp >= cpu - 5 || r.GpuTemp >= gpu - 5;
        bool want = boost ? warm : hot;
        if (want != boost)
        {
            boost = want;
            ApplyFans(all: true);
        }
        lock (fanLock) r.FanBoost = boost && fanTargets.Count > 0;
    }

    /// <summary>En quittant : les ventilateurs réglés par Temperator reviennent au BIOS / au pilote.</summary>
    void RestoreFans()
    {
        if (fanTouched.Count == 0) return;
        try
        {
            var controls = Controls();
            foreach (var id in fanTouched)
                if (controls.TryGetValue(id, out var s))
                    try { s.Control?.SetDefault(); } catch { }
        }
        catch { }
        fanTouched.Clear();
    }

    /// <summary>
    /// Chaque ventilateur, avec son réglage s'il en a un (même matériel, même numéro). Les prises de la carte mère
    /// sans ventilateur (0 tr/min, jamais vu tourner) sont ignorées ; ceux des cartes graphiques restent (arrêt au repos).
    /// </summary>
    List<FanInfo> ReadFans(List<IHardware> hw)
    {
        var list = new List<FanInfo>();
        foreach (var h in hw.SelectMany(Flatten))
        {
            var fans = h.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
            var controls = h.Sensors.Where(s => s.SensorType == SensorType.Control).ToList();
            if (fans.Count + controls.Count == 0) continue;
            bool gpu = h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

            void Add(ISensor? fan, ISensor? control)
            {
                bool settable = control?.Control != null;
                var main = settable ? control! : fan ?? control!;
                string key = main.Identifier.ToString();
                float? rpm = fan != null && Valid(fan) ? fan.Value : null;
                float? duty = control != null && Valid(control) ? control.Value : null;
                bool manual = control?.Control?.ControlMode == ControlMode.Software;
                bool wanted;
                lock (fanLock) wanted = fanTargets.ContainsKey(key);
                if (rpm > 0) fanSeen.Add(key);
                if (!(rpm > 0 || gpu || manual || wanted || fanSeen.Contains(key))) return;
                list.Add(new FanInfo(key, (fan ?? control!).Name, h.Name, rpm, duty, settable ? key : null, manual));
            }

            foreach (var f in fans) Add(f, controls.FirstOrDefault(c => c.Index == f.Index));
            foreach (var c in controls.Where(c => c.Control != null && !fans.Any(f => f.Index == c.Index))) Add(null, c);
        }
        return list;
    }

    static IEnumerable<IHardware> Flatten(IHardware h)
    {
        yield return h;
        foreach (var sub in h.SubHardware)
            foreach (var x in Flatten(sub)) yield return x;
    }

    // ------------------------------------------------------------------ mémoire : Windows, si LibreHardwareMonitor n'a rien donné

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>Niveau de batterie par Windows si LibreHardwareMonitor ne le donne pas (et état « sur secteur »).</summary>
    static void BatteryFallback(Readings r)
    {
        try
        {
            var p = Forms.SystemInformation.PowerStatus;
            if (p.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) || p.BatteryChargeStatus == Forms.BatteryChargeStatus.Unknown)
            {
                r.BatteryLevel = null;
                return;
            }
            r.BatteryLevel ??= p.BatteryLifePercent * 100;
            if (p.PowerLineStatus == Forms.PowerLineStatus.Online && r.BatteryRate is not < 0) r.BatteryCharging = p.BatteryLifePercent < 1 || r.BatteryCharging;
        }
        catch { }
    }

    static void RamFallback(Readings r)
    {
        if (r.RamLoad.HasValue && r.RamTotal.HasValue) return;
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m)) return;
        const float gb = 1024f * 1024f * 1024f;
        r.RamTotal = m.TotalPhys / gb;
        r.RamUsed = (m.TotalPhys - m.AvailPhys) / gb;
        r.RamLoad = m.MemoryLoad;
    }

    public void Dispose()
    {
        stopping = true;
        wake.Set();
        thread.Join(4000);
    }
}
