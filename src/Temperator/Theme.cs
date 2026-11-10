using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace Temperator;

static class Theme
{
    public static readonly Brush Accent = Make(0x4E, 0xD8, 0xC0);
    public static readonly Brush Warn = Make(0xFF, 0xB5, 0x47);
    public static readonly Brush Hot = Make(0xFF, 0x5A, 0x5F);
    public static readonly Brush Text = Make(0xF1, 0xF3, 0xF6);
    public static readonly Brush Soft = Make(0xC9, 0xCF, 0xD8);
    public static readonly Brush Muted = Make(0x8E, 0x97, 0xA4);
    public static readonly Brush Track = Make(0xF1, 0xF3, 0xF6, 0x24);
    public static readonly FontFamily TitleFont = new("Bahnschrift, Segoe UI");

    static SolidColorBrush Make(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Couleur d'une température (toujours comparée en °C) : normal, chaud, très chaud.</summary>
    public static Brush ForTemp(float? celsius, float warn, float hot) =>
        celsius is not float c ? Muted : c >= hot ? Hot : c >= warn ? Warn : Accent;
}

/// <summary>Mise en forme des valeurs, en français.</summary>
static class Fmt
{
    public static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static float Temp(float celsius, bool fahrenheit) => fahrenheit ? celsius * 9 / 5 + 32 : celsius;

    static string N(float x, int decimals) => x.ToString("N" + decimals, Fr);

    // les types sont comparés par leur nom : le code reste valable si LibreHardwareMonitor en ajoute ou en retire
    public static string Value(string type, float? value, bool fahrenheit)
    {
        if (value is not float x || float.IsNaN(x) || float.IsInfinity(x)) return "—";
        return type switch
        {
            "Temperature" => $"{N(Temp(x, fahrenheit), 0)} {(fahrenheit ? "°F" : "°C")}",
            "Load" or "Control" or "Level" or "Humidity" => $"{N(x, 0)} %",
            "Clock" => $"{N(x, 0)} MHz",
            "Frequency" => $"{N(x, 0)} Hz",
            "Power" => $"{N(x, 1)} W",
            "Voltage" => $"{N(x, 3)} V",
            "Current" => $"{N(x, 2)} A",
            "Fan" => $"{N(x, 0)} tr/min",
            "Flow" => $"{N(x, 0)} L/h",
            "Data" => $"{N(x, 1)} Go",
            "SmallData" => $"{N(x, 0)} Mo",
            "Throughput" => Rate(x),
            "Energy" => $"{N(x, 0)} mWh",
            "Noise" => $"{N(x, 0)} dBA",
            "TimeSpan" => x is >= 0 and < 1e8f ? TimeSpan.FromSeconds(x).ToString(@"d\.hh\:mm\:ss").TrimStart('0', '.') : N(x, 0) + " s",
            _ => N(x, 2),
        };
    }

    static string Rate(float bytes) =>
        bytes >= 1e9f ? $"{N(bytes / 1e9f, 1)} Go/s" :
        bytes >= 1e6f ? $"{N(bytes / 1e6f, 1)} Mo/s" :
        bytes >= 1e3f ? $"{N(bytes / 1e3f, 0)} Ko/s" : $"{N(bytes, 0)} o/s";

    public static readonly string[] GroupOrder =
        { "Temperature", "Load", "Clock", "Power", "Fan", "Control", "Voltage", "Current", "Data", "SmallData", "Throughput", "Level", "Frequency", "Factor", "Energy", "Noise", "TimeSpan" };

    public static string Group(string type) => type switch
    {
        "Temperature" => "TEMPÉRATURES",
        "Load" => "UTILISATION",
        "Clock" => "FRÉQUENCES",
        "Power" => "PUISSANCE",
        "Fan" => "VENTILATEURS",
        "Control" => "COMMANDE DES VENTILATEURS",
        "Voltage" => "TENSIONS",
        "Current" => "COURANT",
        "Data" => "DONNÉES",
        "SmallData" => "MÉMOIRE",
        "Throughput" => "DÉBIT",
        "Level" => "NIVEAUX",
        "Frequency" => "FRÉQUENCES (HZ)",
        "Factor" => "FACTEURS",
        "Energy" => "ÉNERGIE",
        "Noise" => "BRUIT",
        "TimeSpan" => "DURÉES",
        _ => type.ToUpperInvariant(),
    };

    public static string Kind(IHardware h) => h.HardwareType.ToString() switch
    {
        "Cpu" => "CPU",
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => "GPU",
        "Memory" => "MÉMOIRE",
        "Motherboard" => "CARTE MÈRE",
        "SuperIO" => "CAPTEURS",
        "Storage" => "DISQUE",
        "Network" => "RÉSEAU",
        "Cooler" => "REFROIDISSEMENT",
        "EmbeddedController" => "CONTRÔLEUR",
        "Psu" => "ALIMENTATION",
        "Battery" => "BATTERIE",
        var t => t.ToUpperInvariant(),
    };

    public static string MachineLine()
    {
        string os = "Windows";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out int build);
            os = (build >= 22000 ? "Windows 11" : "Windows 10") + (k?.GetValue("DisplayVersion") is string dv ? " " + dv : "");
        }
        catch { }
        return $"{Environment.MachineName}  ·  {os}  ·  Temperator {App.Version}";
    }
}

static class Native
{
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>Fenêtre « outil » : pas dans Alt+Tab ni dans la barre des tâches.</summary>
    public static void MakeToolWindow(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr((ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
    }

    public static readonly Lazy<bool> PawnIOInstalled = new(() =>
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"); return k != null; }
        catch { return true; }
    });
}
