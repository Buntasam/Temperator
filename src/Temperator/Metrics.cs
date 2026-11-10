using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Temperator;

/// <summary>Ce qu'un élément du widget montre en ce moment — commun aux lignes, aux jauges et à la barre fine.</summary>
sealed class MetricView
{
    /// <summary>La donnée existe sur ce PC (sinon l'élément est caché).</summary>
    public bool Available;
    public string Value = "—", Unit = "";
    public Brush ValueBrush = Brushes.White;
    /// <summary>Lignes : barre en % (null : pas de barre) et texte à droite.</summary>
    public float? BarPercent;
    public Brush? BarBrush;
    public string? Side;
    /// <summary>Jauges : remplissage de l'anneau (0 à 1) et texte sous la valeur.</summary>
    public double Ring;
    public Brush? RingBrush;
    public string? Sub;
    /// <summary>Jauges : valeur et unité plus courtes si besoin (null : les mêmes qu'en lignes).</summary>
    public string? TileValue, TileUnit;
    public string? Tip;
    /// <summary>Température à surveiller pour le feu et le rouge (null : aucune).</summary>
    public float? Celsius;
    public float HotLimit;
}

/// <summary>
/// Calcule, pour chaque élément, ce qu'il faut afficher à partir des lectures : un seul endroit pour la logique
/// (couleurs, seuils, unités, échelles), quelle que soit la disposition.
/// </summary>
sealed class MetricViews
{
    readonly Palette palette;
    readonly Func<AppSettings> settings;
    float netPeak = 1e6f, powerPeak = 150, fpsPeak = 60;

    public MetricViews(Palette palette, Func<AppSettings> settings)
    {
        this.palette = palette;
        this.settings = settings;
    }

    AppSettings S => settings();
    public float CpuHot => (float)S.CpuHot;
    public float GpuHot => (float)S.GpuHot;
    public float DiskHot => (float)S.DiskHot;

    public MetricView Build(string key, Readings r, float? fps) => key switch
    {
        MetricKeys.Cpu => Temperature(r.CpuTemp, r.CpuLoad, CpuHot, 15, Math.Max(100, CpuHot + 10),
                              Lines(r.CpuName, r.CpuPower is float p ? $"{L.T("Consommation", "Power")} : {N(p, "0.#")} W" : null), r.CpuName != null || r.CpuTemp.HasValue),
        MetricKeys.Gpu => Temperature(r.GpuTemp, r.GpuLoad, GpuHot, 12, Math.Max(95, GpuHot + 8),
                              Lines(r.GpuName, r.GpuPower is float gp ? $"{L.T("Consommation", "Power")} : {N(gp, "0.#")} W" : null), r.GpuName != null),
        MetricKeys.Disk => Temperature(r.DiskTemp, r.DiskLoad, DiskHot, 10, Math.Max(70, DiskHot + 10),
                              r.DiskName is null ? L.T("Activité du disque le plus sollicité", "Activity of the busiest drive")
                                  : (S.DiskId != null ? r.DiskName : L.T($"Le plus chaud : {r.DiskName}", $"Hottest: {r.DiskName}")) + L.T("\nUtilisation : activité du disque", "\nLoad: drive activity"),
                              r.DiskTemp.HasValue || r.DiskLoad.HasValue),
        MetricKeys.Ram => Ram(r),
        MetricKeys.Fan => Fan(r),
        MetricKeys.Net => Net(r),
        MetricKeys.Battery => Battery(r),
        MetricKeys.Power => Power(r),
        MetricKeys.Fps => Fps(fps),
        _ => new MetricView(),
    };

    /// <summary>CPU, GPU, disque : température colorée, barre = utilisation, anneau = température de 20 °C au maximum.</summary>
    MetricView Temperature(float? celsius, float? load, float hot, float warnGap, float ringMax, string? tip, bool available)
    {
        bool f = S.Fahrenheit;
        var brush = palette.ForTemp(celsius, hot - warnGap, hot);
        return new MetricView
        {
            Available = available,
            Value = celsius is float c ? N(Fmt.Temp(c, f), "0") : "—",
            Unit = f ? "°F" : "°",
            ValueBrush = brush,
            BarPercent = load,
            BarBrush = load >= 90 ? palette.Warn : palette.Accent,
            Side = load is float l ? $"{Math.Clamp(l, 0, 100):0} %" : null,
            Ring = celsius is float v ? (v - 20) / (ringMax - 20) : 0,
            RingBrush = brush,
            Sub = load is float l2 ? $"{Math.Clamp(l2, 0, 100):0} %" : null,
            Tip = tip,
            Celsius = celsius,
            HotLimit = hot,
        };
    }

    MetricView Ram(Readings r) => new()
    {
        Available = r.RamLoad.HasValue,
        Value = r.RamUsed is float u ? N(u, u >= 100 ? "0" : "0.0") : "—",
        Unit = " " + Fmt.GB,
        ValueBrush = palette.Text,
        BarPercent = r.RamLoad,
        BarBrush = r.RamLoad >= 90 ? palette.Warn : palette.Accent,
        Side = r.RamLoad is float l ? $"{l:0} %" : null,
        Ring = (r.RamLoad ?? 0) / 100.0,
        RingBrush = r.RamLoad >= 90 ? palette.Warn : palette.Accent,
        Sub = r.RamLoad is float l2 ? $"{l2:0} %" : null,
        Tip = r.RamUsed is float used && r.RamTotal is float total
            ? L.T($"Mémoire : {N(used, "0.0")} Go utilisés sur {N(total, "0.0")} Go", $"Memory: {N(used, "0.0")} GB used of {N(total, "0.0")} GB")
            : L.T("Mémoire", "Memory"),
    };

    MetricView Fan(Readings r) => new()
    {
        Available = r.FanRpm.HasValue,
        Value = r.FanRpm is float rpm ? N(rpm, "N0") : "—",
        Unit = " " + Fmt.Rpm,
        ValueBrush = palette.Text,
        Ring = (r.FanRpm ?? 0) / 4000.0,
        RingBrush = palette.Accent,
        Sub = Fmt.Rpm,
        TileUnit = "",
        Tip = L.T("Le ventilateur le plus rapide", "The fastest fan"),
    };

    /// <summary>Réseau : ↓ réception, ↑ envoi ; l'anneau suit la réception par rapport au pic récent.</summary>
    MetricView Net(Readings r)
    {
        float downB = r.NetDown ?? 0;
        netPeak = Math.Max(netPeak * 0.995f, Math.Max(downB, 1e6f));
        var (down, unit) = Fmt.RateParts(downB);
        var (up, upUnit) = Fmt.RateParts(r.NetUp ?? 0);
        return new MetricView
        {
            Available = r.NetDown.HasValue || r.NetUp.HasValue,
            Value = $"↓ {down}  ↑ {up}",
            Unit = upUnit == unit ? " " + unit : $" {unit} / {upUnit}",
            ValueBrush = palette.Text,
            Ring = downB / netPeak,
            RingBrush = palette.Accent,
            Sub = $"{unit} ↓",
            TileValue = down,
            TileUnit = "",
            Tip = Lines(r.NetName, L.T($"Réception : {down} {unit}\nEnvoi : {up} {upUnit}", $"Download: {down} {unit}\nUpload: {up} {upUnit}")),
        };
    }

    /// <summary>Batterie : niveau (rouge sous 20 %), « ⚡ » en charge, ou les watts consommés.</summary>
    MetricView Battery(Readings r)
    {
        var brush = r.BatteryLevel < 20 ? palette.Hot : r.BatteryLevel < 35 ? palette.Warn : palette.Accent;
        string side = r.BatteryCharging ? "⚡" : r.BatteryRate is float rate && rate < 0 ? $"{N(rate, "0")} W" : "";
        string state = r.BatteryCharging ? L.T("En charge", "Charging") : L.T("Sur batterie", "On battery");
        return new MetricView
        {
            Available = r.BatteryLevel.HasValue,
            Value = r.BatteryLevel is float lvl ? N(lvl, "0") : "—",
            Unit = " %",
            ValueBrush = r.BatteryLevel < 20 ? palette.Hot : palette.Text,
            BarPercent = r.BatteryLevel,
            BarBrush = brush,
            Side = side,
            Ring = (r.BatteryLevel ?? 0) / 100.0,
            RingBrush = brush,
            Sub = side.Length > 0 ? side : null,
            Tip = r.BatteryRate is float w ? $"{state} · {N(Math.Abs(w), "0.#")} W" : state,
        };
    }

    MetricView Power(Readings r)
    {
        float watts = r.TotalPower ?? 0;
        powerPeak = Math.Max(powerPeak, watts);
        return new MetricView
        {
            Available = r.TotalPower.HasValue,
            Value = N(watts, "0"),
            Unit = " W",
            ValueBrush = palette.Text,
            Ring = watts / powerPeak,
            RingBrush = watts > 0.85 * powerPeak ? palette.Warn : palette.Accent,
            Sub = "CPU+GPU",
            Tip = L.T("Consommation estimée du processeur et de la carte graphique", "Estimated power of the processor and graphics card") +
                  $"\nCPU {(r.CpuPower is float c ? N(c, "0") + " W" : "—")} · GPU {(r.GpuPower is float g ? N(g, "0") + " W" : "—")}",
        };
    }

    /// <summary>FPS : rouge sous 30, orange sous 50 ; l'anneau suit le pic de la partie en cours.</summary>
    MetricView Fps(float? fps)
    {
        if (fps is float v) fpsPeak = Math.Max(fpsPeak, v);
        var brush = fps is not float x ? palette.Muted : x < 30 ? palette.Hot : x < 50 ? palette.Warn : palette.Accent;
        return new MetricView
        {
            Available = fps.HasValue,
            Value = fps is float f ? N(f, "0") : "—",
            Unit = " FPS",
            ValueBrush = brush,
            Ring = (fps ?? 0) / fpsPeak,
            RingBrush = brush,
            Sub = "FPS",
            TileUnit = "",
            Tip = L.T("Images par seconde du jeu en plein écran (jeux DirectX)", "Frames per second of the fullscreen game (DirectX games)"),
        };
    }

    /// <summary>Le pic de FPS repart de zéro à chaque nouvelle partie.</summary>
    public void ResetFpsPeak() => fpsPeak = 60;

    static string N(float x, string format) => x.ToString(format, Fmt.Fr);

    public static string? Lines(params string?[] lines)
    {
        var text = string.Join("\n", Array.FindAll(lines, l => !string.IsNullOrEmpty(l)));
        return text.Length > 0 ? text : null;
    }
}

/// <summary>Un élément de la barre fine : NOM valeur, sur une ligne.</summary>
sealed class BarItem
{
    public readonly StackPanel Root = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), Background = Brushes.Transparent };
    readonly TextBlock big;
    readonly Run value = new("…"), unit = new();
    readonly Palette palette;
    bool hot;

    public BarItem(string label, Palette palette)
    {
        this.palette = palette;
        Root.Children.Add(new TextBlock
        {
            Text = label, FontFamily = Theme.TitleFont, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = palette.Muted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0),
        });
        value.FontSize = 14;
        value.FontWeight = FontWeights.SemiBold;
        unit.FontSize = 10;
        unit.Foreground = palette.Muted;
        big = new TextBlock { FontFamily = Theme.TitleFont, VerticalAlignment = VerticalAlignment.Center };
        big.Inlines.Add(value);
        big.Inlines.Add(unit);
        Root.Children.Add(big);
    }

    public bool Visible
    {
        set => Root.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Set(MetricView v)
    {
        if (value.Text != v.Value) value.Text = v.Value;
        string u = v.Unit.Trim();
        unit.Text = u.Length == 0 || u == "°" || u == "°F" ? u : " " + u;
        value.Foreground = v.ValueBrush;
    }

    /// <summary>Trop chaud : la valeur rougeoie (la barre est trop fine pour des flammes).</summary>
    public void SetHot(bool on)
    {
        if (on == hot) return;
        hot = on;
        if (!on) { big.Effect = null; return; }
        var glow = new DropShadowEffect { Color = palette.Hot.Color, ShadowDepth = 0, BlurRadius = 12, Opacity = 0.9 };
        var pulse = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(520)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(pulse, 30);
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
        big.Effect = glow;
    }
}
