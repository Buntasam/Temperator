using System;
using System.Windows;
using System.Windows.Controls;
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
    // seuils en °C (réglables) : rouge, flammes et alertes à partir de Hot ; orange un peu avant
    float CpuHot => (float)app.Settings.CpuHot;
    float CpuWarn => CpuHot - 15;
    float GpuHot => (float)app.Settings.GpuHot;
    float GpuWarn => GpuHot - 12;
    float DiskHot => (float)app.Settings.DiskHot;
    float DiskWarn => DiskHot - 10;

    readonly App app;
    readonly Palette palette = new();
    readonly MetricRow cpu, gpu, ram, disk, fan, net, bat, pow;
    readonly GaugeTile gCpu, gGpu, gRam, gDisk, gFan, gNet, gBat, gPow;
    readonly Fire fire;
    readonly DispatcherTimer backdropTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DropShadowEffect halo = new() { ShadowDepth = 0, BlurRadius = 6, Opacity = 0.6 };
    readonly TopGuard guard;
    readonly DispatcherTimer ctrlTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly DispatcherTimer gameTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    // infobulles avec la courbe des 5 dernières minutes (une par ligne et une par jauge)
    readonly HistoryTip cpuTip, gpuTip, ramTip, diskTip, gCpuTip, gGpuTip, gRamTip, gDiskTip;
    bool hotCpu, hotGpu, hotDisk;
    bool menuOpen, passThrough;
    bool gameActive, hiddenByGame;
    string? hotkeyRegistered;
    float netPeak = 1e6f, powerPeak = 150;
    int updates;

    // largeur estimée avant le premier affichage, pour éviter un saut de position
    const double EstimatedWidth = 218 + 28 + 2 + 24;

    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();

        string diskLabel = L.T("DISQUE", "DISK"), fanLabel = L.T("VENTILO", "FAN"), netLabel = L.T("RÉSEAU", "NET"),
               batLabel = L.T("BATTERIE", "BATTERY"), powLabel = L.T("CONSO", "POWER");
        cpu = new("CPU", palette); gpu = new("GPU", palette); ram = new("RAM", palette); disk = new(diskLabel, palette);
        fan = new(fanLabel, palette); net = new(netLabel, palette); bat = new(batLabel, palette); pow = new(powLabel, palette);
        foreach (var row in new[] { cpu, gpu, ram, disk, fan, net, bat, pow }) Rows.Children.Add(row.Root);
        fan.Wide(); net.Wide(); pow.Wide();
        foreach (var row in new[] { gpu, disk, fan, net, bat, pow }) row.Visible = false;   // affichés dès qu'on sait qu'ils existent
        cpu.Visible = app.Settings.ShowCpu;
        ram.Visible = app.Settings.ShowRam;

        gCpu = new("CPU", palette); gGpu = new("GPU", palette); gRam = new("RAM", palette); gDisk = new(diskLabel, palette);
        gFan = new(fanLabel, palette); gNet = new(netLabel, palette); gBat = new(batLabel, palette); gPow = new(powLabel, palette);
        foreach (var tile in Tiles) Gauges.Children.Add(tile.Root);
        foreach (var tile in new[] { gGpu, gDisk, gFan, gNet, gBat, gPow }) tile.Visible = false;
        gCpu.Visible = app.Settings.ShowCpu;
        gRam.Visible = app.Settings.ShowRam;

        Card.BorderBrush = palette.Line;
        LogoArc.Stroke = palette.Accent;
        LogoDot.Fill = palette.Text;
        Pulse.Fill = palette.Accent;
        Brand.Foreground = palette.Muted;
        Notice.Foreground = palette.Warn;
        fire = new Fire(FireLayer);
        guard = new TopGuard(this);
        Bg.SizeChanged += (_, e) => FireLayer.Clip = new RectangleGeometry(new Rect(e.NewSize), 14, 14);
        backdropTimer.Tick += (_, _) => SampleBackdrop(animate: true);
        ctrlTimer.Tick += (_, _) => UpdateClickThrough();
        gameTimer.Tick += (_, _) => CheckGame();

        var h = app.History;
        bool F() => app.Settings.Fahrenheit;
        cpuTip = new(h, h.CpuTemp, true, F, () => CpuHot); gCpuTip = new(h, h.CpuTemp, true, F, () => CpuHot);
        gpuTip = new(h, h.GpuTemp, true, F, () => GpuHot); gGpuTip = new(h, h.GpuTemp, true, F, () => GpuHot);
        ramTip = new(h, h.RamLoad, false, F, null); gRamTip = new(h, h.RamLoad, false, F, null);
        diskTip = new(h, h.DiskTemp, true, F, () => DiskHot); gDiskTip = new(h, h.DiskTemp, true, F, () => DiskHot);
        Tip(cpu.Root, cpuTip); Tip(gpu.Root, gpuTip); Tip(ram.Root, ramTip); Tip(disk.Root, diskTip);
        Tip(gCpu.Root, gCpuTip); Tip(gGpu.Root, gGpuTip); Tip(gRam.Root, gRamTip); Tip(gDisk.Root, gDiskTip);

        var s = app.Settings;
        if (s.Left is double l && s.Top is double t) { Left = l; Top = t; }
        else { Left = SystemParameters.WorkArea.Right - EstimatedWidth + 4; Top = SystemParameters.WorkArea.Top - 4; }
        ApplySettings();

        SourceInitialized += (_, _) =>
        {
            Native.MakeToolWindow(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
            RegisterHotkey();
            if (app.Settings.ClickThrough) UpdateClickThrough();
            if (!app.Snapshotting) gameTimer.Start();
        };
        Loaded += (_, _) => EnsureOnScreen();
        SizeChanged += (_, e) =>
        {
            if (app.Settings.Left == null) { PlaceDefault(); return; }
            // collé au bord droit : on garde le bord droit en place quand la largeur change (affichage, taille…)
            double dw = e.NewSize.Width - e.PreviousSize.Width;
            if (e.PreviousSize.Width > 0 && Math.Abs(dw) > 0.5 && Left + e.PreviousSize.Width >= SystemParameters.WorkArea.Right - 40)
            {
                Left -= dw;
                app.Settings.Left = Left;
            }
        };
        IsVisibleChanged += (_, _) => UpdateBackdropTimer();
        Root.MouseLeftButtonDown += OnLeftDown;
        Root.MouseRightButtonUp += (_, e) => { app.ShowMenu(Root); e.Handled = true; };
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(EnsureOnScreen);
    }

    GaugeTile[] Tiles => new[] { gCpu, gGpu, gRam, gDisk, gFan, gNet, gBat, gPow };

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

    // ------------------------------------------------------------------ mode jeu : un jeu ou une vidéo en plein écran sur le même écran

    /// <summary>Plein écran en cours, en mode « compact » : CPU et GPU seulement, plus petit, fond léger.</summary>
    bool Compact => gameActive && app.Settings.GameMode == "compact";

    void CheckGame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        bool full = app.Settings.GameMode != "off" && hwnd != IntPtr.Zero && Fullscreen.OnWidgetScreen(hwnd);
        if (full == gameActive) return;
        gameActive = full;
        if (app.Settings.GameMode == "hide")
        {
            if (full && IsVisible) { hiddenByGame = true; Hide(); }
            else if (!full && hiddenByGame) { hiddenByGame = false; Show(); }
        }
        else if (!full && hiddenByGame) { hiddenByGame = false; Show(); }
        ApplySettings();
        Apply(app.Last);
    }

    // ------------------------------------------------------------------ apparence

    /// <summary>Opacité réelle du fond : 0 en mode « aucun fond », sinon le réglage « Opacité du fond » (léger en jeu).</summary>
    double BgOpacity => app.Settings.Transparent ? 0 : Compact ? Math.Min(app.Settings.BackgroundOpacity, 0.35) : app.Settings.BackgroundOpacity;

    /// <summary>
    /// Les couleurs suivent le fond de l'écran : en translucide, sans fond, ou dès que le fond devient assez
    /// transparent pour laisser voir ce qu'il y a derrière.
    /// </summary>
    bool Adaptive => app.Settings.Translucent || BgOpacity < 0.6;

    public void ApplySettings()
    {
        var s = app.Settings;
        Topmost = s.Topmost || Compact;
        guard.Enable((s.Topmost && s.TopmostForce || Compact) && !app.Snapshotting);
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = s.Opacity;
        Zoom.ScaleX = Zoom.ScaleY = s.Scale * (Compact ? 0.85 : 1);
        Header.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        palette.SetAccent(Palette.Parse(s.Accent));

        // affichage : lignes et barres, ou jauges rondes
        Rows.Visibility = s.Gauges ? Visibility.Collapsed : Visibility.Visible;
        Gauges.Visibility = s.Gauges ? Visibility.Visible : Visibility.Collapsed;
        Body.Width = s.Gauges ? double.NaN : 218;
        Body.MinWidth = s.Gauges ? (Compact ? 0 : 150) : 0;

        // fond : verre teinté ou dégradé sombre, avec sa propre opacité (l'ombre suit)
        double bg = BgOpacity;
        Bg.Background = s.Translucent ? palette.Tint : (Brush)FindResource("CardBg");
        Bg.Opacity = bg;
        Shadow.Opacity = s.Translucent ? 0.16 : 0.5;
        Card.BorderBrush = bg >= 0.15 ? palette.Line : Brushes.Transparent;

        if (Adaptive)
        {
            halo.BlurRadius = bg < 0.3 ? 9 : 6;   // moins il y a de fond, plus le halo porte la lisibilité
            Body.Effect = halo;
            SampleBackdrop(animate: false);
        }
        else
        {
            Body.Effect = null;
            palette.Use(light: false, animate: false);
        }
        halo.Opacity = HaloOpacity;
        UpdateBackdropTimer();
        RegisterHotkey();
        if (s.ClickThrough && !app.Snapshotting) ctrlTimer.Start();
        else ctrlTimer.Stop();
        UpdateClickThrough();
        if (!s.FireAnimation || s.Gauges) fire.Set(0);
        if (!s.FireAnimation || !s.Gauges) foreach (var t in Tiles) t.SetFire(0);
    }

    double HaloOpacity => BgOpacity < 0.3 ? 0.95 : palette.IsLight ? 0.75 : 0.6;

    /// <summary>Menu ouvert : la garde du premier plan ne passe pas devant, et le widget reste cliquable.</summary>
    public void MenuOpen(bool open)
    {
        menuOpen = open;
        guard.Paused = open;
        UpdateClickThrough();
    }

    // ------------------------------------------------------------------ clics traversants : la souris passe à travers, sauf Ctrl enfoncé

    void UpdateClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        bool want = app.Settings.ClickThrough && !menuOpen && !Native.CtrlDown();
        if (want == passThrough) return;
        passThrough = want;
        Native.SetClickThrough(hwnd, want);
    }

    // ------------------------------------------------------------------ raccourci clavier global : afficher / masquer

    const int HotkeyId = 0x5445;

    void RegisterHotkey()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        string wanted = app.Snapshotting ? "" : app.Settings.Hotkey ?? "";
        if (hwnd == IntPtr.Zero || wanted == hotkeyRegistered) return;
        Native.UnregisterHotKey(hwnd, HotkeyId);
        hotkeyRegistered = wanted;
        if (wanted.Length == 0) return;
        if (!Native.ParseHotkey(wanted, out uint mods, out uint key) || !Native.RegisterHotKey(hwnd, HotkeyId, mods | Native.MOD_NOREPEAT, key))
            app.Notify(L.T("Raccourci indisponible", "Shortcut unavailable"),
                L.T($"{wanted.Replace("Shift", "Maj")} est déjà utilisé par une autre application. Choisis-en un autre dans les réglages.",
                    $"{wanted} is already used by another app. Pick another one in the settings."),
                warning: true);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            hiddenByGame = false;
            app.ToggleWidget();
            handled = true;
        }
        return IntPtr.Zero;
    }

    static void Tip(FrameworkElement element, HistoryTip tip)
    {
        element.ToolTip = tip;
        ToolTipService.SetShowDuration(element, 60000);
    }

    void UpdateBackdropTimer()
    {
        if (Adaptive && IsVisible) backdropTimer.Start();
        else backdropTimer.Stop();
    }

    void SampleBackdrop(bool animate)
    {
        if (!Adaptive || app.Snapshotting) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (Backdrop.Around(hwnd) is { } sample) ApplyBackdrop(sample, animate);
        else if (!animate) ApplyBackdrop(Backdrop.Of(Color.FromRgb(0x20, 0x24, 0x2C)), false);   // rien à lire : verre sombre
    }

    internal bool LightPalette => palette.IsLight;

    /// <summary>
    /// Adapte le widget au fond : palette claire ou sombre selon la luminosité (avec une marge pour ne pas
    /// clignoter), verre teinté de la couleur du fond, et plus opaque si le fond est chargé.
    /// </summary>
    internal void ApplyBackdrop(Backdrop.Sample bg, bool animate)
    {
        bool light = palette.IsLight ? bg.Luma > 0.42 : bg.Luma > 0.6;
        if (light != palette.IsLight || !animate)
        {
            palette.Use(light, animate);
            var haloColor = palette.Halo;
            if (animate) halo.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(haloColor, TimeSpan.FromMilliseconds(450)));
            else { halo.BeginAnimation(DropShadowEffect.ColorProperty, null); halo.Color = haloColor; }
            halo.Opacity = HaloOpacity;
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
        bool compact = Compact;
        bool showCpu = s.ShowCpu, showGpu = s.ShowGpu, showRam = s.ShowRam && !compact, showDisk = s.ShowDisk && !compact,
             showFan = s.ShowFan && !compact, showNet = s.ShowNet && !compact, showBat = s.ShowBattery && !compact, showPow = s.ShowPower && !compact;

        cpu.Visible = showCpu;
        SetTemp(cpu, r.CpuTemp, f, CpuWarn, CpuHot);
        cpu.SetLoad(r.CpuLoad);
        cpuTip.Text = gCpuTip.Text = Lines(r.CpuName, r.CpuPower is float p ? L.T("Consommation", "Power") + $" : {p.ToString("0.#", Fmt.Fr)} W" : null);

        gpu.Visible = showGpu && r.GpuName != null;
        SetTemp(gpu, r.GpuTemp, f, GpuWarn, GpuHot);
        gpu.SetLoad(r.GpuLoad);
        gpuTip.Text = gGpuTip.Text = Lines(r.GpuName, r.GpuPower is float gp ? L.T("Consommation", "Power") + $" : {gp.ToString("0.#", Fmt.Fr)} W" : null);

        ram.Visible = showRam && r.RamLoad.HasValue;
        ram.SetValue(r.RamUsed is float u ? u.ToString(u >= 100 ? "0" : "0.0", Fmt.Fr) : "—", " " + Fmt.GB, palette.Text);
        ram.SetLoad(r.RamLoad);
        ramTip.Text = gRamTip.Text = r.RamUsed is float used && r.RamTotal is float total
            ? L.T($"Mémoire : {used.ToString("0.0", Fmt.Fr)} Go utilisés sur {total.ToString("0.0", Fmt.Fr)} Go",
                  $"Memory: {used.ToString("0.0", Fmt.Fr)} GB used of {total.ToString("0.0", Fmt.Fr)} GB")
            : L.T("Mémoire", "Memory");

        disk.Visible = showDisk && (r.DiskTemp.HasValue || r.DiskLoad.HasValue);
        SetTemp(disk, r.DiskTemp, f, DiskWarn, DiskHot);
        disk.SetLoad(r.DiskLoad);
        diskTip.Text = gDiskTip.Text = r.DiskName is null
            ? L.T("Activité du disque le plus sollicité", "Activity of the busiest drive")
            : (s.DiskId != null ? r.DiskName : L.T($"Le plus chaud : {r.DiskName}", $"Hottest: {r.DiskName}")) + L.T("\nUtilisation : activité du disque", "\nLoad: drive activity");

        fan.Visible = showFan && r.FanRpm.HasValue;
        fan.SetValue(r.FanRpm is float rpm ? rpm.ToString("N0", Fmt.Fr) : "—", " " + Fmt.Rpm, palette.Text);

        // réseau : ↓ réception, ↑ envoi (même unité pour les deux)
        net.Visible = showNet && (r.NetDown.HasValue || r.NetUp.HasValue);
        var (down, unit) = Fmt.RateParts(r.NetDown ?? 0);
        var (up, upUnit) = Fmt.RateParts(r.NetUp ?? 0);
        net.SetValue($"↓ {down}  ↑ {up}", upUnit == unit ? " " + unit : $" {unit} / {upUnit}", palette.Text);
        net.Root.ToolTip = r.NetName;

        // batterie : barre = niveau (rouge sous 20 %), à droite « ⚡ » en charge ou les watts consommés
        bat.Visible = showBat && r.BatteryLevel.HasValue;
        var batBrush = r.BatteryLevel < 20 ? palette.Hot : r.BatteryLevel < 35 ? palette.Warn : palette.Accent;
        bat.SetValue(r.BatteryLevel is float lvl ? lvl.ToString("0", Fmt.Fr) : "—", " %", r.BatteryLevel < 20 ? palette.Hot : palette.Text);
        bat.SetBar(r.BatteryLevel, batBrush, BatterySide(r));
        bat.Root.ToolTip = BatteryTip(r);

        pow.Visible = showPow && r.TotalPower.HasValue;
        pow.SetValue(r.TotalPower is float w ? w.ToString("0", Fmt.Fr) : "—", " W", palette.Text);
        pow.Root.ToolTip = PowerTip(r);

        // trop chaud : la valeur rougeoie et le widget prend feu (avec 3 °C de marge pour s'éteindre)
        bool c = Hot(ref hotCpu, r.CpuTemp, CpuHot) && showCpu;
        bool g = Hot(ref hotGpu, r.GpuTemp, GpuHot) && showGpu;
        bool d = Hot(ref hotDisk, r.DiskTemp, DiskHot) && showDisk;
        cpu.SetHot(c); gpu.SetHot(g); disk.SetHot(d);
        gCpu.SetHot(c); gGpu.SetHot(g); gDisk.SetHot(d);
        ApplyGauges(r, showCpu, showGpu, showRam, showDisk, showFan, showNet, showBat, showPow);
        // le feu : sur la jauge concernée en jauges rondes, sur tout le bas du widget en lignes
        double Flame(double over) => s.FireAnimation ? 1 + Math.Clamp(over / 10, 0, 1) : 0;
        if (s.Gauges)
        {
            fire.Set(0);
            gCpu.SetFire(c ? Flame(Over(c, r.CpuTemp, CpuHot)) : 0);
            gGpu.SetFire(g ? Flame(Over(g, r.GpuTemp, GpuHot)) : 0);
            gDisk.SetFire(d ? Flame(Over(d, r.DiskTemp, DiskHot)) : 0);
        }
        else
        {
            double over = Math.Max(Over(c, r.CpuTemp, CpuHot), Math.Max(Over(g, r.GpuTemp, GpuHot), Over(d, r.DiskTemp, DiskHot)));
            fire.Set(c || g || d ? Flame(over) : 0);
        }

        string? notice = null;
        if (!app.IsAdmin && !app.Demo) notice = L.T("Lance Temperator en administrateur pour lire les températures.", "Run Temperator as administrator to read temperatures.");
        else if (s.ShowCpu && r.CpuTemp == null && ++updates > 3 && !Native.PawnIOInstalled.Value)
            notice = L.T("Pilote PawnIO absent : température du CPU indisponible. Relance l'installeur de Temperator.",
                         "PawnIO driver missing: CPU temperature unavailable. Run the Temperator installer again.");
        if (compact) notice = null;
        Notice.Text = notice ?? "";
        Notice.Visibility = notice == null ? Visibility.Collapsed : Visibility.Visible;

        Pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)));
    }

    static string BatterySide(Readings r) =>
        r.BatteryCharging ? "⚡" : r.BatteryRate is float rate && rate < 0 ? $"{rate.ToString("0", Fmt.Fr)} W" : "";

    static string BatteryTip(Readings r)
    {
        string state = r.BatteryCharging ? L.T("En charge", "Charging") : L.T("Sur batterie", "On battery");
        return r.BatteryRate is float rate ? $"{state} · {Math.Abs(rate).ToString("0.#", Fmt.Fr)} W" : state;
    }

    static string PowerTip(Readings r) =>
        L.T("Consommation estimée du processeur et de la carte graphique", "Estimated power of the processor and graphics card") +
        $"\nCPU {(r.CpuPower is float c ? c.ToString("0", Fmt.Fr) + " W" : "—")} · GPU {(r.GpuPower is float g ? g.ToString("0", Fmt.Fr) + " W" : "—")}";

    /// <summary>Jauges rondes : l'anneau suit la température (20 °C → maximum du composant), la charge est écrite dessous.</summary>
    void ApplyGauges(Readings r, bool showCpu, bool showGpu, bool showRam, bool showDisk, bool showFan, bool showNet, bool showBat, bool showPow)
    {
        var s = app.Settings;
        bool f = s.Fahrenheit;
        string deg = f ? "°F" : "°";
        string T(float? c) => c is float v ? Fmt.Temp(v, f).ToString("0", Fmt.Fr) : "—";
        string? P(float? l) => l is float v ? $"{Math.Clamp(v, 0, 100):0} %" : null;
        static double Scale(float? c, float max) => c is float v ? (v - 20) / (max - 20) : 0;

        gCpu.Visible = showCpu;
        var cb = palette.ForTemp(r.CpuTemp, CpuWarn, CpuHot);
        gCpu.Set(T(r.CpuTemp), deg, cb, Scale(r.CpuTemp, Math.Max(100, CpuHot + 10)), cb, P(r.CpuLoad));

        gGpu.Visible = showGpu && r.GpuName != null;
        var gb = palette.ForTemp(r.GpuTemp, GpuWarn, GpuHot);
        gGpu.Set(T(r.GpuTemp), deg, gb, Scale(r.GpuTemp, Math.Max(95, GpuHot + 8)), gb, P(r.GpuLoad));

        gRam.Visible = showRam && r.RamLoad.HasValue;
        gRam.Set(r.RamUsed is float u ? u.ToString(u >= 100 ? "0" : "0.0", Fmt.Fr) : "—", " " + Fmt.GB, palette.Text,
                 (r.RamLoad ?? 0) / 100.0, r.RamLoad >= 90 ? palette.Warn : palette.Accent, P(r.RamLoad));

        gDisk.Visible = showDisk && (r.DiskTemp.HasValue || r.DiskLoad.HasValue);
        var db = palette.ForTemp(r.DiskTemp, DiskWarn, DiskHot);
        gDisk.Set(T(r.DiskTemp), deg, db, Scale(r.DiskTemp, Math.Max(70, DiskHot + 10)), db, P(r.DiskLoad));

        gFan.Visible = showFan && r.FanRpm.HasValue;
        gFan.Set(r.FanRpm is float rpm ? rpm.ToString("N0", Fmt.Fr) : "—", "", palette.Text, (r.FanRpm ?? 0) / 4000.0, palette.Accent, Fmt.Rpm);
        gFan.ToolTip = L.T("Le ventilateur le plus rapide", "The fastest fan");

        // réseau : l'anneau suit la réception, par rapport au pic récent
        gNet.Visible = showNet && (r.NetDown.HasValue || r.NetUp.HasValue);
        float downB = r.NetDown ?? 0;
        netPeak = Math.Max(netPeak * 0.995f, Math.Max(downB, 1e6f));
        var (down, unit) = Fmt.RateParts(downB);
        var (up, upUnit) = Fmt.RateParts(r.NetUp ?? 0);
        gNet.Set(down, "", palette.Text, downB / netPeak, palette.Accent, $"{unit} ↓");
        gNet.ToolTip = Lines(r.NetName, L.T($"Réception : {down} {unit}\nEnvoi : {up} {upUnit}", $"Download: {down} {unit}\nUpload: {up} {upUnit}"));

        gBat.Visible = showBat && r.BatteryLevel.HasValue;
        var batBrush = r.BatteryLevel < 20 ? palette.Hot : r.BatteryLevel < 35 ? palette.Warn : palette.Accent;
        string side = BatterySide(r);
        gBat.Set(r.BatteryLevel is float lvl ? lvl.ToString("0", Fmt.Fr) : "—", " %", r.BatteryLevel < 20 ? palette.Hot : palette.Text,
                 (r.BatteryLevel ?? 0) / 100.0, batBrush, side.Length > 0 ? side : null);
        gBat.ToolTip = BatteryTip(r);

        gPow.Visible = showPow && r.TotalPower.HasValue;
        float watts = r.TotalPower ?? 0;
        powerPeak = Math.Max(powerPeak, watts);
        gPow.Set(watts.ToString("0", Fmt.Fr), " W", palette.Text, watts / powerPeak, watts > 0.85 * powerPeak ? palette.Warn : palette.Accent, "CPU+GPU");
        gPow.ToolTip = PowerTip(r);
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
