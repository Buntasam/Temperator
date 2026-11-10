using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>Un élément du widget, sous ses trois formes (ligne, jauge, barre fine).</summary>
    sealed class Item
    {
        public required string Key;
        public required MetricRow Row;
        public required GaugeTile Tile;
        public required BarItem Bar;
        public HistoryTip? RowTip, TileTip;
        public bool Hot;
    }

    readonly App app;
    readonly Palette palette = new();
    readonly MetricViews views;
    readonly Dictionary<string, Item> items = new();
    readonly Fire fire;
    readonly FpsCounter fps = new();
    readonly DispatcherTimer backdropTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer ctrlTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly DispatcherTimer gameTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DropShadowEffect halo = new() { ShadowDepth = 0, BlurRadius = 6, Opacity = 0.6 };
    readonly TopGuard guard;
    bool menuOpen, passThrough;
    bool gameActive, hiddenByGame;
    int gamePid;
    string? hotkeyRegistered, orderApplied;
    int updates;

    // largeur estimée avant le premier affichage, pour éviter un saut de position
    const double EstimatedWidth = 218 + 28 + 2 + 24;

    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        views = new MetricViews(palette, () => app.Settings);

        var h = app.History;
        bool F() => app.Settings.Fahrenheit;
        foreach (var key in MetricKeys.All)
        {
            string label = Label(key);
            var item = new Item { Key = key, Row = new MetricRow(label, palette), Tile = new GaugeTile(label, palette), Bar = new BarItem(label, palette) };
            if (key is MetricKeys.Fan or MetricKeys.Net or MetricKeys.Power or MetricKeys.Fps) item.Row.Wide();

            // infobulles avec la courbe des 5 dernières minutes
            (Series series, bool temp, Func<double>? limit)? history = key switch
            {
                MetricKeys.Cpu => (h.CpuTemp, true, () => views.CpuHot),
                MetricKeys.Gpu => (h.GpuTemp, true, () => views.GpuHot),
                MetricKeys.Disk => (h.DiskTemp, true, () => views.DiskHot),
                MetricKeys.Ram => (h.RamLoad, false, null),
                _ => null,
            };
            if (history is var (series, temp, limit))
            {
                item.RowTip = new HistoryTip(h, series, temp, F, limit);
                item.TileTip = new HistoryTip(h, series, temp, F, limit);
                Tip(item.Row.Root, item.RowTip);
                Tip(item.Tile.Root, item.TileTip);
            }
            // affichés dès qu'on sait qu'ils existent (sauf CPU et RAM, toujours là)
            bool start = key is MetricKeys.Cpu or MetricKeys.Ram && app.Settings.Shows(key);
            item.Row.Visible = item.Tile.Visible = item.Bar.Visible = start;
            items[key] = item;
        }

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
        Closed += (_, _) => fps.Dispose();
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
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (app.Settings.Theme == AppSettings.FollowWindows) Dispatcher.InvokeAsync(() => { ApplySettings(); Apply(app.Last); });
        };
    }

    static string Label(string key) => key switch
    {
        MetricKeys.Disk => L.T("DISQUE", "DISK"),
        MetricKeys.Fan => L.T("VENTILO", "FAN"),
        MetricKeys.Net => L.T("RÉSEAU", "NET"),
        MetricKeys.Battery => L.T("BATTERIE", "BATTERY"),
        MetricKeys.Power => L.T("CONSO", "POWER"),
        _ => key.ToUpperInvariant(),
    };

    static void Tip(FrameworkElement element, HistoryTip tip)
    {
        element.ToolTip = tip;
        ToolTipService.SetShowDuration(element, 60000);
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

    // ------------------------------------------------------------------ plein écran : profil « Jeu » et FPS

    /// <summary>Un jeu ou une vidéo est en plein écran, et le profil « Jeu » s'applique.</summary>
    bool Profile => gameActive && app.Settings.GameMode == AppSettings.GameProfile;

    /// <summary>L'élément est-il affiché, compte tenu du profil en cours ?</summary>
    bool Shows(string key) => Profile ? app.Settings.GameShow.Contains(key) : app.Settings.Shows(key);

    string Layout => Profile ? app.Settings.GameLayout ?? app.Settings.Layout : app.Settings.Layout;

    void CheckGame()
    {
        var s = app.Settings;
        var hwnd = new WindowInteropHelper(this).Handle;
        bool watch = s.GameMode != AppSettings.GameOff || s.ShowFps;
        bool full = watch && hwnd != IntPtr.Zero && Fullscreen.OnWidgetScreen(hwnd, out gamePid);
        if (full == gameActive) return;
        gameActive = full;

        if (s.GameMode == AppSettings.GameHide)
        {
            if (full && IsVisible) { hiddenByGame = true; Hide(); }
            else if (!full && hiddenByGame) { hiddenByGame = false; Show(); }
        }
        else if (!full && hiddenByGame) { hiddenByGame = false; Show(); }

        // FPS : on n'écoute DirectX que pendant le plein écran, et seulement si on les affiche
        if (full && Shows(MetricKeys.Fps) && !app.Snapshotting) { views.ResetFpsPeak(); fps.Start(); }
        else fps.Stop();

        ApplySettings();
        Apply(app.Last);
    }

    // ------------------------------------------------------------------ apparence

    /// <summary>Opacité réelle du fond : 0 sans fond, sinon le réglage (celui du profil « Jeu » en plein écran).</summary>
    double BgOpacity => app.Settings.Transparent ? 0 : Profile ? app.Settings.GameBackgroundOpacity : app.Settings.BackgroundOpacity;

    /// <summary>
    /// Les couleurs suivent le fond de l'écran : en translucide, sans fond, ou dès que le fond devient assez
    /// transparent pour laisser voir ce qu'il y a derrière.
    /// </summary>
    bool Adaptive => app.Settings.Translucent || BgOpacity < 0.6;

    /// <summary>Thème clair du fond opaque (choisi, ou celui de Windows).</summary>
    bool LightTheme => app.Settings.Theme == AppSettings.Light || app.Settings.Theme == AppSettings.FollowWindows && WindowsUsesLightTheme();

    static bool WindowsUsesLightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    public void ApplySettings()
    {
        var s = app.Settings;
        bool profile = Profile;
        string layout = Layout;
        Topmost = s.Topmost || profile;
        guard.Enable((s.Topmost && s.TopmostForce || profile) && !app.Snapshotting);
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = s.Opacity;
        Zoom.ScaleX = Zoom.ScaleY = profile ? s.GameScale : s.Scale;
        palette.SetAccent(Palette.Parse(s.Accent));
        ApplyOrder();

        // disposition : jauges rondes, lignes et barres, ou barre fine
        Rows.Visibility = layout == AppSettings.Rows ? Visibility.Visible : Visibility.Collapsed;
        Gauges.Visibility = layout == AppSettings.Gauges ? Visibility.Visible : Visibility.Collapsed;
        BarPanel.Visibility = layout == AppSettings.Bar ? Visibility.Visible : Visibility.Collapsed;
        Header.Visibility = profile || layout == AppSettings.Bar ? Visibility.Collapsed : Visibility.Visible;
        Body.Width = layout == AppSettings.Rows ? 218 : double.NaN;
        Body.MinWidth = layout == AppSettings.Gauges && !profile ? 150 : 0;
        Card.Padding = layout == AppSettings.Bar ? new Thickness(12, 5, 12, 6) : new Thickness(14, 10, 14, 10);

        // fond : verre teinté, dégradé sombre ou clair, avec sa propre opacité (l'ombre suit)
        double bg = BgOpacity;
        bool light = LightTheme;
        Bg.Background = s.Translucent ? palette.Tint : (Brush)FindResource(light ? "CardBgLight" : "CardBg");
        Bg.Opacity = bg;
        Shadow.Opacity = s.Translucent ? 0.16 : light ? 0.3 : 0.5;
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
            palette.Use(light, animate: false);
        }
        halo.Opacity = HaloOpacity;
        UpdateBackdropTimer();
        RegisterHotkey();
        if (s.ClickThrough && !app.Snapshotting) ctrlTimer.Start();
        else ctrlTimer.Stop();
        UpdateClickThrough();
        if (!s.FireAnimation || layout != AppSettings.Rows) fire.Set(0);
        if (!s.FireAnimation || layout != AppSettings.Gauges) foreach (var it in items.Values) it.Tile.SetFire(0);
    }

    /// <summary>Range les éléments dans l'ordre choisi (lignes, jauges et barre).</summary>
    void ApplyOrder()
    {
        var order = app.Settings.Order;
        string signature = string.Join(",", order);
        if (signature == orderApplied) return;
        orderApplied = signature;
        Rows.Children.Clear();
        Gauges.Children.Clear();
        BarPanel.Children.Clear();
        foreach (var key in order)
        {
            if (!items.TryGetValue(key, out var it)) continue;
            Rows.Children.Add(it.Row.Root);
            Gauges.Children.Add(it.Tile.Root);
            BarPanel.Children.Add(it.Bar.Root);
        }
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

    // ------------------------------------------------------------------ couleurs adaptées au fond de l'écran

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

    /// <summary>Valeur FPS imposée (captures d'écran) ; null : mesure réelle.</summary>
    internal float? DemoFps;

    public void Apply(Readings r)
    {
        if (!r.Ready) return;
        var s = app.Settings;
        string layout = Layout;
        float? framesPerSecond = DemoFps ?? (gameActive && fps.Running ? fps.Sample(gamePid) : null);
        double Flame(double over) => s.FireAnimation ? 1 + Math.Clamp(over / 10, 0, 1) : 0;
        double hottest = -1;

        foreach (var it in items.Values)
        {
            var v = views.Build(it.Key, r, framesPerSecond);
            bool show = Shows(it.Key) && v.Available;
            it.Row.Visible = it.Tile.Visible = it.Bar.Visible = show;

            // trop chaud : la valeur rougeoie (avec 3 °C de marge pour s'éteindre)
            it.Hot = show && Hot(it.Hot, v.Celsius, v.HotLimit);
            it.Row.SetHot(it.Hot);
            it.Tile.SetHot(it.Hot);
            it.Bar.SetHot(it.Hot);
            double over = it.Hot && v.Celsius is float c ? c - v.HotLimit : -1;
            if (it.Hot) hottest = Math.Max(hottest, over);
            it.Tile.SetFire(layout == AppSettings.Gauges && it.Hot ? Flame(over) : 0);   // jauges : le feu sur la jauge concernée
            if (!show) continue;

            it.Row.SetValue(v.Value, v.Unit, v.ValueBrush);
            if (v.BarPercent.HasValue || v.BarBrush != null) it.Row.SetBar(v.BarPercent, v.BarBrush ?? palette.Accent, v.Side ?? "");
            it.Tile.Set(v.TileValue ?? v.Value, v.TileUnit ?? v.Unit, v.ValueBrush, v.Ring, v.RingBrush ?? palette.Accent, v.Sub);
            it.Bar.Set(v);
            it.Bar.Root.ToolTip = v.Tip;
            if (it.RowTip != null) it.RowTip.Text = it.TileTip!.Text = v.Tip;
            else it.Row.Root.ToolTip = it.Tile.Root.ToolTip = v.Tip;
        }
        // lignes : tout le bas du widget prend feu
        fire.Set(layout == AppSettings.Rows && hottest >= 0 ? Flame(hottest) : 0);

        string? notice = null;
        if (!app.IsAdmin && !app.Demo) notice = L.T("Lance Temperator en administrateur pour lire les températures.", "Run Temperator as administrator to read temperatures.");
        else if (s.ShowCpu && r.CpuTemp == null && ++updates > 3 && !Native.PawnIOInstalled.Value)
            notice = L.T("Pilote PawnIO absent : température du CPU indisponible. Relance l'installeur de Temperator.",
                         "PawnIO driver missing: CPU temperature unavailable. Run the Temperator installer again.");
        if (Profile || layout == AppSettings.Bar) notice = null;
        Notice.Text = notice ?? "";
        Notice.Visibility = notice == null ? Visibility.Collapsed : Visibility.Visible;

        Pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)));
    }

    /// <summary>Trop chaud au-delà du seuil, et le reste jusqu'à 3 °C en dessous (pas de clignotement).</summary>
    internal static bool Hot(bool wasHot, float? celsius, float limit) =>
        limit > 0 && celsius is float v && (v >= limit || (wasHot && v >= limit - 3));

    /// <summary>Captures : force le profil « Jeu » (plein écran simulé).</summary>
    internal void SimulateGame(bool on)
    {
        gameActive = on;
        ApplySettings();
        Apply(app.Last);
    }
}
