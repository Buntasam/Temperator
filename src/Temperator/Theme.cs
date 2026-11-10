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

/// <summary>Mise en forme des valeurs, dans la langue de l'interface.</summary>
static class Fmt
{
    /// <summary>Culture des nombres (fr-FR ou en-US, selon la langue).</summary>
    public static CultureInfo Fr => L.Culture;

    public static float Temp(float celsius, bool fahrenheit) => fahrenheit ? celsius * 9 / 5 + 32 : celsius;

    static string N(float x, int decimals) => x.ToString("N" + decimals, Fr);

    public static string GB => L.T("Go", "GB");
    public static string MB => L.T("Mo", "MB");
    public static string Rpm => L.T("tr/min", "RPM");

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
            "Fan" => $"{N(x, 0)} {Rpm}",
            "Flow" => $"{N(x, 0)} L/h",
            "Data" => $"{N(x, 1)} {GB}",
            "SmallData" => $"{N(x, 0)} {MB}",
            "Throughput" => Rate(x),
            "Energy" => $"{N(x, 0)} mWh",
            "Noise" => $"{N(x, 0)} dBA",
            "TimeSpan" => x is >= 0 and < 1e8f ? TimeSpan.FromSeconds(x).ToString(@"d\.hh\:mm\:ss").TrimStart('0', '.') : N(x, 0) + " s",
            _ => N(x, 2),
        };
    }

    public static string Rate(float bytes)
    {
        var (v, u) = RateParts(bytes);
        return v + " " + u;
    }

    /// <summary>Débit en octets/s → (valeur, unité), par ex. ("12,3", "Mo/s").</summary>
    public static (string Value, string Unit) RateParts(float bytes)
    {
        string o = L.T("o", "B"), k = L.T("Ko", "KB"), m = L.T("Mo", "MB"), g = L.T("Go", "GB");
        return bytes >= 1e9f ? (N(bytes / 1e9f, 1), g + "/s") :
               bytes >= 1e6f ? (N(bytes / 1e6f, bytes >= 1e8f ? 0 : 1), m + "/s") :
               bytes >= 1e3f ? (N(bytes / 1e3f, 0), k + "/s") : (N(bytes, 0), o + "/s");
    }

    public static readonly string[] GroupOrder =
        { "Temperature", "Load", "Clock", "Power", "Fan", "Control", "Voltage", "Current", "Data", "SmallData", "Throughput", "Level", "Frequency", "Factor", "Energy", "Noise", "TimeSpan" };

    public static string Group(string type) => type switch
    {
        "Temperature" => L.T("TEMPÉRATURES", "TEMPERATURES"),
        "Load" => L.T("UTILISATION", "LOAD"),
        "Clock" => L.T("FRÉQUENCES", "CLOCKS"),
        "Power" => L.T("PUISSANCE", "POWER"),
        "Fan" => L.T("VENTILATEURS", "FANS"),
        "Control" => L.T("COMMANDE DES VENTILATEURS", "FAN CONTROL"),
        "Voltage" => L.T("TENSIONS", "VOLTAGES"),
        "Current" => L.T("COURANT", "CURRENT"),
        "Data" => L.T("DONNÉES", "DATA"),
        "SmallData" => L.T("MÉMOIRE", "MEMORY"),
        "Throughput" => L.T("DÉBIT", "THROUGHPUT"),
        "Level" => L.T("NIVEAUX", "LEVELS"),
        "Frequency" => L.T("FRÉQUENCES (HZ)", "FREQUENCIES (HZ)"),
        "Factor" => L.T("FACTEURS", "FACTORS"),
        "Energy" => L.T("ÉNERGIE", "ENERGY"),
        "Noise" => L.T("BRUIT", "NOISE"),
        "TimeSpan" => L.T("DURÉES", "DURATIONS"),
        _ => type.ToUpperInvariant(),
    };

    public static string Kind(IHardware h) => h.HardwareType.ToString() switch
    {
        "Cpu" => "CPU",
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => "GPU",
        "Memory" => L.T("MÉMOIRE", "MEMORY"),
        "Motherboard" => L.T("CARTE MÈRE", "MOTHERBOARD"),
        "SuperIO" => L.T("CAPTEURS", "SENSORS"),
        "Storage" => L.T("DISQUE", "DRIVE"),
        "Network" => L.T("RÉSEAU", "NETWORK"),
        "Cooler" => L.T("REFROIDISSEMENT", "COOLING"),
        "EmbeddedController" => L.T("CONTRÔLEUR", "CONTROLLER"),
        "Psu" => L.T("ALIMENTATION", "POWER SUPPLY"),
        "Battery" => L.T("BATTERIE", "BATTERY"),
        var t => t.ToUpperInvariant(),
    };

    public static string OsName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out int build);
            return (build >= 22000 ? "Windows 11" : "Windows 10") + (k?.GetValue("DisplayVersion") is string dv ? " " + dv : "") + $" (build {build})";
        }
        catch { return "Windows"; }
    }

    public static string MachineLine() => $"{Environment.MachineName}  ·  {OsName().Split(" (")[0]}  ·  Temperator {App.Version}";
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

    // ------------------------------------------------------------------ clics traversants

    const long WS_EX_TRANSPARENT = 0x20;

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);

    public static bool CtrlDown() => (GetAsyncKeyState(0x11) & 0x8000) != 0;   // VK_CONTROL

    /// <summary>La souris passe à travers la fenêtre (la fenêtre est déjà « layered » : WPF transparent).</summary>
    public static void SetClickThrough(IntPtr hwnd, bool on)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    // ------------------------------------------------------------------ raccourci clavier global

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    /// <summary>"Ctrl+Alt+T" → modificateurs + touche (lettre ou chiffre).</summary>
    public static bool ParseHotkey(string text, out uint mods, out uint key)
    {
        mods = 0;
        key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift" or "maj": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (part.Length != 1 || !char.IsLetterOrDigit(part[0])) return false;
                    key = char.ToUpperInvariant(part[0]);
                    break;
            }
        }
        return key != 0 && mods != 0;
    }

    public static readonly Lazy<bool> PawnIOInstalled = new(() =>
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"); return k != null; }
        catch { return true; }
    });
}
