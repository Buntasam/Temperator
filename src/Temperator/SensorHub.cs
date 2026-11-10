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
}

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
    readonly AutoResetEvent wake = new(false);
    volatile bool stopping;
    bool loggedError;

    /// <summary>À verrouiller pour parcourir le matériel depuis l'interface (le thread capteurs le modifie).</summary>
    public readonly object Sync = new();
    public int IntervalMs { get; set; } = 1000;
    /// <summary>Matériel choisi dans les réglages (null : automatique).</summary>
    public volatile string? GpuId, DiskId, NetId;
    public bool Ready { get; private set; }
    public event Action<Readings>? Updated;

    public SensorHub(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        thread = new Thread(Loop) { IsBackground = true, Name = "Capteurs", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>Matériel détecté (appeler sous lock(Sync)).</summary>
    public IReadOnlyList<IHardware> Hardware => Ready ? computer.Hardware.ToList() : Array.Empty<IHardware>();

    void Loop()
    {
        try
        {
            lock (Sync) computer.Open();
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
                        foreach (var h in hw) Update(h);
                        r = Read(hw, GpuId, DiskId, NetId);
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
            wake.WaitOne(Math.Clamp(IntervalMs, 250, 10000));
        }

        lock (Sync)
        {
            try { computer.Close(); } catch { }
        }
    }

    static void Update(IHardware h)
    {
        h.Update();
        foreach (var sub in h.SubHardware) Update(sub);
    }

    // ------------------------------------------------------------------ choix des capteurs à afficher

    static Readings Read(List<IHardware> hw, string? gpuId, string? diskId, string? netId)
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
            r.GpuPower = Find(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Board Power", "GPU Core") ?? Max(gpu, SensorType.Power);
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
        { "virtual", "pseudo", "loopback", "miniport", "filter", "hyper-v", "vethernet", "npcap", "kernel debug", "teredo", "isatap", "6to4", "vmware", "virtualbox", "tap-", "wintun", "bluetooth" };

    static HashSet<string> realAdapters = new();
    static DateTime realAdaptersAt = DateTime.MinValue;

    /// <summary>
    /// Noms des vraies cartes réseau (Wi-Fi, Ethernet…), d'après Windows : la description dit si une carte est virtuelle
    /// (« Ethernet 2 » peut être une carte VirtualBox). Recalculé chaque minute (carte branchée, Wi-Fi activé…).
    /// </summary>
    static HashSet<string> RealAdapters()
    {
        if (DateTime.UtcNow - realAdaptersAt < TimeSpan.FromMinutes(1)) return realAdapters;
        realAdaptersAt = DateTime.UtcNow;
        try
        {
            realAdapters = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not (System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                            && !VirtualAdapters.Any(v => n.Description.Contains(v, StringComparison.OrdinalIgnoreCase) || n.Name.Contains(v, StringComparison.OrdinalIgnoreCase)))
                .Select(n => n.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return realAdapters;
    }

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
