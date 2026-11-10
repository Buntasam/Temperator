using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
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
    };
    readonly Dispatcher dispatcher;
    readonly Thread thread;
    readonly AutoResetEvent wake = new(false);
    volatile bool stopping;
    bool loggedError;

    /// <summary>À verrouiller pour parcourir le matériel depuis l'interface (le thread capteurs le modifie).</summary>
    public readonly object Sync = new();
    public int IntervalMs { get; set; } = 1000;
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
                        r = Read(hw);
                    }
                }
                catch (Exception ex)
                {
                    if (!loggedError) { App.Log(ex); loggedError = true; }
                }
            }
            RamFallback(r);
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

    static Readings Read(List<IHardware> hw)
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

        // carte graphique dédiée en priorité, sinon la puce intégrée
        var gpu = hw.FirstOrDefault(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
                  ?? hw.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
        if (gpu != null)
        {
            r.GpuName = gpu.Name;
            r.GpuTemp = Find(gpu, SensorType.Temperature, "GPU Core", "GPU Hot Spot") ?? Max(gpu, SensorType.Temperature);
            r.GpuLoad = Find(gpu, SensorType.Load, "GPU Core", "D3D 3D") ?? Max(gpu, SensorType.Load);
        }

        var mem = hw.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && !h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        if (mem != null)
        {
            r.RamLoad = Find(mem, SensorType.Load, "Memory");
            r.RamUsed = Find(mem, SensorType.Data, "Memory Used", "Used Memory");
            var avail = Find(mem, SensorType.Data, "Memory Available", "Available Memory");
            if (r.RamUsed.HasValue && avail.HasValue) r.RamTotal = r.RamUsed + avail;
        }

        // disques : le plus chaud, et l'activité la plus forte
        foreach (var d in hw.Where(h => h.HardwareType == HardwareType.Storage))
        {
            var t = Find(d, SensorType.Temperature, "Temperature", "Composite Temperature") ?? Max(d, SensorType.Temperature);
            if (t.HasValue && (r.DiskTemp == null || t > r.DiskTemp)) { r.DiskTemp = t; r.DiskName = d.Name; }
            var a = Find(d, SensorType.Load, "Total Activity");
            if (a.HasValue && (r.DiskLoad == null || a > r.DiskLoad)) r.DiskLoad = a;
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
