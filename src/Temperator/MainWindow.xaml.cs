using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Temperator;

/// <summary>Le widget, en haut à droite de l'écran.</summary>
public partial class MainWindow : Window
{
    // seuils en °C : orange à partir de Warn, rouge (et flammes) à partir de Hot
    const float CpuWarn = 70, CpuHot = 85, GpuWarn = 75, GpuHot = 87, DiskWarn = 50, DiskHot = 60;

    readonly App app;
    readonly Palette palette = new();
    readonly MetricRow cpu, gpu, ram, disk, fan;
    readonly Fire fire;
    readonly DispatcherTimer backdropTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DropShadowEffect halo = new() { ShadowDepth = 0, BlurRadius = 6, Opacity = 0.6 };
    bool hotCpu, hotGpu, hotDisk;
    int updates;

    // largeur estimée avant le premier affichage, pour éviter un saut de position
    const double EstimatedWidth = 218 + 28 + 2 + 24;

    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();

        cpu = new("CPU", palette); gpu = new("GPU", palette); ram = new("RAM", palette);
        disk = new("DISQUE", palette); fan = new("VENTILO", palette);
        foreach (var row in new[] { cpu, gpu, ram, disk, fan }) Rows.Children.Add(row.Root);
        fan.Wide();
        gpu.Visible = disk.Visible = fan.Visible = false;   // affichés dès qu'on sait qu'ils existent
        cpu.Visible = app.Settings.ShowCpu;
        ram.Visible = app.Settings.ShowRam;

        Card.BorderBrush = palette.Line;
        LogoArc.Stroke = palette.Accent;
        LogoDot.Fill = palette.Text;
        Pulse.Fill = palette.Accent;
        Brand.Foreground = palette.Muted;
        Notice.Foreground = palette.Warn;
        fire = new Fire(FireLayer);
        Bg.SizeChanged += (_, e) => FireLayer.Clip = new RectangleGeometry(new Rect(e.NewSize), 14, 14);
        backdropTimer.Tick += (_, _) => SampleBackdrop(animate: true);

        var s = app.Settings;
        if (s.Left is double l && s.Top is double t) { Left = l; Top = t; }
        else { Left = SystemParameters.WorkArea.Right - EstimatedWidth + 4; Top = SystemParameters.WorkArea.Top - 4; }
        ApplySettings();

        SourceInitialized += (_, _) => Native.MakeToolWindow(this);
        Loaded += (_, _) => EnsureOnScreen();
        SizeChanged += (_, _) => { if (app.Settings.Left == null) PlaceDefault(); };
        IsVisibleChanged += (_, _) => UpdateBackdropTimer();
        Root.MouseLeftButtonDown += OnLeftDown;
        Root.MouseRightButtonUp += (_, e) => { app.ShowMenu(Root); e.Handled = true; };
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(EnsureOnScreen);
    }

    // ------------------------------------------------------------------ position

    void PlaceDefault()
    {
        if (app.Snapshotting) return;
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth + 4;
        Top = wa.Top - 4;
    }

    public void ResetPosition()
    {
        app.Settings.Left = app.Settings.Top = null;
        app.Settings.Save();
        PlaceDefault();
        if (!IsVisible) app.SetHidden(false);
        Flash();
    }

    bool OnScreen(double left, double top)
    {
        double w = ActualWidth > 0 ? ActualWidth : EstimatedWidth;
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        return left + w - 40 > vl && left + 40 < vl + SystemParameters.VirtualScreenWidth &&
               top + 12 > vt && top + 40 < vt + SystemParameters.VirtualScreenHeight;
    }

    /// <summary>Écran débranché, résolution changée : on ramène le widget à sa place par défaut.</summary>
    void EnsureOnScreen()
    {
        if (app.Snapshotting) return;
        if (app.Settings.Left == null) PlaceDefault();
        else if (!OnScreen(Left, Top))
        {
            app.Settings.Left = app.Settings.Top = null;
            PlaceDefault();
        }
    }

    void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { app.ShowMap(); return; }
        if (app.Settings.Locked) return;
        double l = Left, t = Top;
        try { DragMove(); } catch (InvalidOperationException) { }   // les couleurs s'adaptent pendant le déplacement
        if (Math.Abs(Left - l) > 1 || Math.Abs(Top - t) > 1)
        {
            app.Settings.Left = Left;
            app.Settings.Top = Top;
            app.Settings.Save();
            SampleBackdrop(animate: true);
        }
    }

    // ------------------------------------------------------------------ apparence

    public void ApplySettings()
    {
        var s = app.Settings;
        Topmost = s.Topmost;
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = s.Opacity;
        Zoom.ScaleX = Zoom.ScaleY = s.Scale;

        if (s.Translucent)
        {
            Bg.Background = palette.Tint;
            Shadow.Opacity = 0.16;
            Body.Effect = halo;   // halo discret autour du texte : lisible sur n'importe quel fond
            SampleBackdrop(animate: false);
        }
        else
        {
            Bg.Background = (Brush)FindResource("CardBg");
            Shadow.Opacity = 0.5;
            Body.Effect = null;
            palette.Use(light: false, animate: false);
        }
        UpdateBackdropTimer();
        if (!s.FireAnimation) fire.Set(0);
    }

    void UpdateBackdropTimer()
    {
        if (app.Settings.Translucent && IsVisible) backdropTimer.Start();
        else backdropTimer.Stop();
    }

    void SampleBackdrop(bool animate)
    {
        if (!app.Settings.Translucent || app.Snapshotting) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (Backdrop.Around(hwnd) is { } sample) ApplyBackdrop(sample, animate);
        else if (!animate) ApplyBackdrop(Backdrop.Of(Color.FromRgb(0x20, 0x24, 0x2C)), false);   // rien à lire : verre sombre
    }

    /// <summary>
    /// Adapte le mode translucide au fond : palette claire ou sombre selon la luminosité (avec une marge pour ne pas
    /// clignoter), verre teinté de la couleur du fond, et plus opaque si le fond est chargé.
    /// </summary>
    internal bool LightPalette => palette.IsLight;

    internal void ApplyBackdrop(Backdrop.Sample bg, bool animate)
    {
        bool light = palette.IsLight ? bg.Luma > 0.42 : bg.Luma > 0.6;
        if (light != palette.IsLight || !animate)
        {
            palette.Use(light, animate);
            var haloColor = palette.Halo;
            if (animate) halo.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(haloColor, TimeSpan.FromMilliseconds(450)));
            else { halo.BeginAnimation(DropShadowEffect.ColorProperty, null); halo.Color = haloColor; }
            halo.Opacity = light ? 0.75 : 0.6;
        }
        var glass = light ? Color.FromRgb(0xFA, 0xFB, 0xFD) : Color.FromRgb(0x0A, 0x0C, 0x10);
        var tint = Palette.Mix(bg.Average, glass, light ? 0.6 : 0.62);
        tint.A = (byte)(255 * Math.Clamp(0.45 + 0.4 * bg.Busy, 0.45, 0.85));
        palette.SetTint(tint, animate);
    }

    /// <summary>Petit clignotement pour montrer où est le widget.</summary>
    public void Flash()
    {
        var a = new DoubleAnimation(0.25, app.Settings.Opacity, TimeSpan.FromMilliseconds(320)) { RepeatBehavior = new RepeatBehavior(2) };
        a.Completed += (_, _) => { Root.BeginAnimation(OpacityProperty, null); Root.Opacity = app.Settings.Opacity; };
        Root.BeginAnimation(OpacityProperty, a);
        Activate();
    }

    // ------------------------------------------------------------------ valeurs

    public void Apply(Readings r)
    {
        if (!r.Ready) return;
        var s = app.Settings;
        bool f = s.Fahrenheit;

        cpu.Visible = s.ShowCpu;
        SetTemp(cpu, r.CpuTemp, f, CpuWarn, CpuHot);
        cpu.SetLoad(r.CpuLoad);
        cpu.Root.ToolTip = Lines(r.CpuName, r.CpuPower is float p ? $"Consommation : {p.ToString("0.#", Fmt.Fr)} W" : null);

        gpu.Visible = s.ShowGpu && r.GpuName != null;
        SetTemp(gpu, r.GpuTemp, f, GpuWarn, GpuHot);
        gpu.SetLoad(r.GpuLoad);
        gpu.Root.ToolTip = r.GpuName;

        ram.Visible = s.ShowRam && r.RamLoad.HasValue;
        ram.SetValue(r.RamUsed is float u ? u.ToString(u >= 100 ? "0" : "0.0", Fmt.Fr) : "—", " Go", palette.Text);
        ram.SetLoad(r.RamLoad);
        ram.Root.ToolTip = r.RamUsed is float used && r.RamTotal is float total
            ? $"{used.ToString("0.0", Fmt.Fr)} Go utilisés sur {total.ToString("0.0", Fmt.Fr)} Go" : null;

        disk.Visible = s.ShowDisk && (r.DiskTemp.HasValue || r.DiskLoad.HasValue);
        SetTemp(disk, r.DiskTemp, f, DiskWarn, DiskHot);
        disk.SetLoad(r.DiskLoad);
        disk.Root.ToolTip = r.DiskName is null ? "Activité du disque le plus sollicité" : $"Le plus chaud : {r.DiskName}\nBarre : activité du disque le plus sollicité";

        fan.Visible = s.ShowFan && r.FanRpm.HasValue;
        fan.SetValue(r.FanRpm is float rpm ? rpm.ToString("N0", Fmt.Fr) : "—", " tr/min", palette.Text);

        // trop chaud : la valeur rougeoie et le widget prend feu (avec 3 °C de marge pour s'éteindre)
        bool c = Hot(ref hotCpu, r.CpuTemp, CpuHot) && s.ShowCpu;
        bool g = Hot(ref hotGpu, r.GpuTemp, GpuHot) && s.ShowGpu;
        bool d = Hot(ref hotDisk, r.DiskTemp, DiskHot) && s.ShowDisk;
        cpu.SetHot(c); gpu.SetHot(g); disk.SetHot(d);
        double over = Math.Max(Over(c, r.CpuTemp, CpuHot), Math.Max(Over(g, r.GpuTemp, GpuHot), Over(d, r.DiskTemp, DiskHot)));
        fire.Set(s.FireAnimation && (c || g || d) ? 1 + Math.Clamp(over / 10, 0, 1) : 0);

        string? notice = null;
        if (!app.IsAdmin && !app.Demo) notice = "Lance Temperator en administrateur pour lire les températures.";
        else if (s.ShowCpu && r.CpuTemp == null && ++updates > 3 && !Native.PawnIOInstalled.Value)
            notice = "Pilote PawnIO absent : température du CPU indisponible. Relance l'installeur de Temperator.";
        Notice.Text = notice ?? "";
        Notice.Visibility = notice == null ? Visibility.Collapsed : Visibility.Visible;

        Pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)));
    }

    static bool Hot(ref bool state, float? celsius, float limit)
    {
        state = celsius is float v && (v >= limit || (state && v >= limit - 3));
        return state;
    }

    static double Over(bool hot, float? celsius, float limit) => hot && celsius is float v ? v - limit : 0;

    void SetTemp(MetricRow row, float? celsius, bool fahrenheit, float warn, float hot) =>
        row.SetValue(celsius is float c ? Fmt.Temp(c, fahrenheit).ToString("0", Fmt.Fr) : "—", fahrenheit ? "°F" : "°", palette.ForTemp(celsius, warn, hot));

    static string? Lines(params string?[] lines)
    {
        var text = string.Join("\n", Array.FindAll(lines, l => !string.IsNullOrEmpty(l)));
        return text.Length > 0 ? text : null;
    }
}
