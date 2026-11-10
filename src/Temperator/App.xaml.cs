using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Temperator;

public partial class App : Application
{
    public static readonly string Version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public AppSettings Settings { get; private set; } = new();
    public SensorHub Hub { get; private set; } = null!;
    public Readings Last { get; private set; } = new();
    public bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    /// <summary>Captures pour le README : valeurs d'exemple au lieu des capteurs.</summary>
    public bool Demo { get; private set; }
    /// <summary>Mode capture : le widget reste hors de l'écran.</summary>
    public bool Snapshotting { get; private set; }

    /// <summary>10 dernières minutes de températures et d'utilisation (infobulles, plan du PC).</summary>
    internal History History { get; } = new();
    Updater updater = null!;
    readonly Dictionary<string, DateTime> hotSince = new(), lastAlert = new();

    MainWindow widget = null!;
    MapWindow? map;
    Tray? tray;
    Mutex? instance;
    EventWaitHandle? showSignal, quitSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);

        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot") { Snapshotting = true; Demo = Array.IndexOf(e.Args, "--demo") > 0; _ = Snapshot(e.Args[1]); return; }

        // une seule instance : la deuxième demande à la première de se montrer, puis s'arrête
        instance = new Mutex(true, @"Global\Temperator.Instance", out bool first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(@"Global\Temperator.Show", out var show)) show.Set();
            Shutdown();
            return;
        }

        Settings = AppSettings.Load();
        showSignal = Listen(@"Global\Temperator.Show", () => SetHidden(false, flash: true));
        quitSignal = Listen(@"Global\Temperator.Quit", Quit);   // utilisé par l'installeur pour fermer proprement

        Hub = new SensorHub(Dispatcher) { IntervalMs = Settings.IntervalMs };
        widget = new MainWindow(this);
        new WindowInteropHelper(widget).EnsureHandle();
        Hub.Updated += r =>
        {
            Last = r;
            History.Add(r);
            widget.Apply(r);
            map?.Refresh();
            tray?.Update(r, Settings);
            CheckHeat(r);
        };
        // toujours visible au lancement (masqué = seulement pour la session en cours)
        Settings.Hidden = false;
        widget.Show();
        tray = new Tray(this);

        if (Settings.LastRunVersion is string previous && previous != Version)
            Notify($"Temperator mis à jour en {Version}", "Clic droit sur le widget pour voir les réglages.");
        Settings.LastRunVersion = Version;
        Settings.Save();
        updater = new Updater(this);
        updater.Start();

        if (Settings.Mode is not (AppSettings.Lite or AppSettings.Full)) ShowWelcome();
    }

    // ------------------------------------------------------------------ mode simple ou complet

    WelcomeWindow? welcome;

    /// <summary>Fenêtre de bienvenue : choisir entre le mode simple et le mode complet.</summary>
    public void ShowWelcome()
    {
        if (welcome != null) { welcome.Activate(); return; }
        welcome = new WelcomeWindow(Settings.Mode ?? AppSettings.Lite);
        welcome.Closed += (_, _) =>
        {
            SetMode(welcome!.Choice, announce: false);
            welcome = null;
            Notify(Settings.IsLite ? "Mode simple" : "Mode complet",
                Settings.IsLite ? "Clic droit sur le widget pour le menu. Tu peux passer en mode complet à tout moment."
                                : "Clic droit sur le widget pour toutes les options.");
        };
        welcome.Show();
    }

    public void SetMode(string mode, bool announce = true)
    {
        Settings.Mode = mode == AppSettings.Full ? AppSettings.Full : AppSettings.Lite;
        if (Settings.IsLite)
        {
            // options cachées en mode simple : on les coupe pour que rien ne reste « coincé »
            Settings.ClickThrough = false;
            Settings.Locked = false;
            Settings.TopmostForce = false;
        }
        Changed();
        if (announce)
            Notify(Settings.IsLite ? "Mode simple" : "Mode complet",
                Settings.IsLite ? "Un menu court, l'essentiel seulement." : "Clic droit sur le widget pour toutes les options.");
    }

    /// <summary>Notification Windows (zone de notification) ; un clic dessus lance onClick.</summary>
    public void Notify(string title, string message, bool warning = false, Action? onClick = null) =>
        tray?.Notify(title, message, warning, onClick);

    // ------------------------------------------------------------------ alertes : trop chaud pendant plus de 30 s (même widget masqué)

    void CheckHeat(Readings r)
    {
        if (!Settings.HeatAlerts || !r.Ready) return;
        Heat("CPU", "Processeur", r.CpuTemp, Settings.CpuHot);
        Heat("GPU", "Carte graphique", r.GpuTemp, Settings.GpuHot);
        Heat("DISK", "Disque", r.DiskTemp, Settings.DiskHot);
    }

    void Heat(string key, string name, float? celsius, double limit)
    {
        var now = DateTime.Now;
        if (celsius is not float c || c < limit)
        {
            hotSince.Remove(key);
            return;
        }
        if (!hotSince.TryGetValue(key, out var since)) { hotSince[key] = now; return; }
        if (now - since < TimeSpan.FromSeconds(30)) return;
        if (lastAlert.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(10)) return;   // pas plus d'une alerte toutes les 10 min
        lastAlert[key] = now;
        bool f = Settings.Fahrenheit;
        string unit = f ? "°F" : "°C";
        Notify($"{name} trop chaud : {Fmt.Temp(c, f):0} {unit}",
            $"Au-dessus de {Fmt.Temp((float)limit, f):0} {unit} depuis plus de 30 secondes. Vérifie la ventilation et ce qui tourne. Clique pour ouvrir le plan du PC.",
            warning: true, onClick: ShowMap);
    }

    EventWaitHandle? Listen(string name, Action action)
    {
        try
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            new Thread(() => { while (handle.WaitOne()) Dispatcher.InvokeAsync(action); }) { IsBackground = true, Name = name }.Start();
            return handle;
        }
        catch (Exception ex) { Log(ex); return null; }
    }

    // ------------------------------------------------------------------ actions

    public void ShowMap()
    {
        if (map == null)
        {
            map = new MapWindow(this);
            map.Closed += (_, _) => map = null;
            map.Show();
        }
        else
        {
            if (map.WindowState == WindowState.Minimized) map.WindowState = WindowState.Normal;
            map.Activate();
        }
    }

    public void SetHidden(bool hidden, bool flash = false)
    {
        Settings.Hidden = hidden;
        Settings.Save();
        if (hidden) widget.Hide();
        else
        {
            widget.Show();
            if (flash) widget.Flash();
        }
    }

    public void ToggleWidget() => SetHidden(widget.IsVisible);

    /// <summary>Un réglage a changé : on l'enregistre et on l'applique partout.</summary>
    public void Changed()
    {
        Settings.Save();
        Hub.IntervalMs = Settings.IntervalMs;
        widget.ApplySettings();
        widget.Apply(Last);
        map?.Refresh();
    }

    public void Quit()
    {
        try { Settings.Save(); } catch { }
        tray?.Dispose();
        tray = null;
        Hub?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        base.OnExit(e);
    }

    // ------------------------------------------------------------------ menu (clic droit sur le widget ou l'icône)

    public void ShowMenu(UIElement? target)
    {
        var menu = BuildMenu();
        menu.Opened += (_, _) => widget.MenuOpen(true);
        menu.Closed += (_, _) => { widget.MenuOpen(false); Settings.Save(); };   // enregistre aussi les curseurs
        menu.Placement = PlacementMode.MousePoint;
        if (target != null) menu.PlacementTarget = target;
        else Native.SetForegroundWindow(new WindowInteropHelper(widget).Handle);   // pour que le menu se ferme en cliquant ailleurs
        menu.IsOpen = true;
    }

    ContextMenu BuildMenu() => Settings.IsLite ? BuildLiteMenu() : BuildFullMenu();

    /// <summary>Mode simple : l'essentiel, rien de plus.</summary>
    ContextMenu BuildLiteMenu()
    {
        var s = Settings;
        var m = new ContextMenu();
        if (updater.Available is string next)
        {
            var up = Item($"Mettre à jour vers la version {next}", () => _ = updater.Install());
            up.FontWeight = FontWeights.SemiBold;
            up.Foreground = Theme.Accent;
            m.Items.Add(up);
        }
        var plan = Item("Plan du PC…", ShowMap);
        plan.FontWeight = FontWeights.SemiBold;
        m.Items.Add(plan);
        m.Items.Add(new Separator());
        m.Items.Add(Choice("Affichage", s.Gauges ? 1 : 0, v => s.Gauges = v == 1, ("Jauges rondes", 1), ("Lignes et barres", 0)));
        m.Items.Add(Choice("Taille", s.Scale, v => s.Scale = v, ("Petite", 0.85), ("Normale", 1.0), ("Grande", 1.2), ("Très grande", 1.45)));
        m.Items.Add(Toggle("Toujours au premier plan", s.Topmost, v => { s.Topmost = v; s.TopmostForce = false; }));
        m.Items.Add(Item("Replacer en haut à droite", () => widget.ResetPosition()));
        m.Items.Add(new Separator());
        var full = Item("Mode complet : toutes les options…", () => SetMode(AppSettings.Full));
        full.Foreground = Theme.Accent;
        m.Items.Add(full);
        m.Items.Add(Item(widget.IsVisible ? "Masquer le widget" : "Afficher le widget", ToggleWidget));
        m.Items.Add(Item("Quitter Temperator", Quit));
        return m;
    }

    /// <summary>Mode complet : toutes les options.</summary>
    ContextMenu BuildFullMenu()
    {
        var s = Settings;
        var m = new ContextMenu();

        if (updater.Available is string next)
        {
            var up = Item($"Mettre à jour vers la version {next}", () => _ = updater.Install());
            up.FontWeight = FontWeights.SemiBold;
            up.Foreground = Theme.Accent;
            m.Items.Add(up);
        }
        var plan = Item("Plan du PC…", ShowMap);
        plan.FontWeight = FontWeights.SemiBold;
        m.Items.Add(plan);
        m.Items.Add(new Separator());

        var show = new MenuItem { Header = "Afficher" };
        show.Items.Add(Toggle("Processeur (CPU)", s.ShowCpu, v => s.ShowCpu = v));
        show.Items.Add(Toggle("Carte graphique (GPU)", s.ShowGpu, v => s.ShowGpu = v));
        show.Items.Add(Toggle("Mémoire (RAM)", s.ShowRam, v => s.ShowRam = v));
        show.Items.Add(Toggle("Disques", s.ShowDisk, v => s.ShowDisk = v));
        show.Items.Add(Toggle("Ventilateur", s.ShowFan, v => s.ShowFan = v));
        m.Items.Add(show);

        m.Items.Add(Choice("Affichage", s.Gauges ? 1 : 0, v => s.Gauges = v == 1,
            ("Lignes et barres", 0), ("Jauges rondes", 1)));
        m.Items.Add(Choice("Taille", s.Scale, v => s.Scale = v,
            ("Petite", 0.85), ("Normale", 1.0), ("Grande", 1.2), ("Très grande", 1.45)));
        m.Items.Add(Sliders("Opacité générale", Percent("", s.Opacity, 0.3, v => s.Opacity = v)));
        m.Items.Add(Choice("Actualisation", s.IntervalMs, v => s.IntervalMs = v,
            ("Toutes les 0,5 s", 500), ("Chaque seconde", 1000), ("Toutes les 2 s", 2000), ("Toutes les 5 s", 5000)));
        m.Items.Add(Toggle("Degrés Fahrenheit", s.Fahrenheit, v => s.Fahrenheit = v));
        m.Items.Add(new Separator());

        m.Items.Add(Choice("Fond", s.Transparent ? 2 : s.Translucent ? 1 : 0, v => { s.Translucent = v == 1; s.Transparent = v == 2; },
            ("Opaque", 0), ("Translucide (s'adapte au fond)", 1), ("Aucun fond, 100 % transparent", 2)));
        var bgOpacity = Sliders("Opacité du fond", Percent("", s.BackgroundOpacity, 0, v => s.BackgroundOpacity = v));
        bgOpacity.IsEnabled = !s.Transparent;
        m.Items.Add(bgOpacity);
        m.Items.Add(Toggle("Flammes quand c'est trop chaud", s.FireAnimation, v => s.FireAnimation = v));
        var heat = Sliders("Seuils « trop chaud »",
            Degrees("CPU", s.CpuHot, 60, 105, v => s.CpuHot = v),
            Degrees("GPU", s.GpuHot, 60, 100, v => s.GpuHot = v),
            Degrees("Disque", s.DiskHot, 40, 80, v => s.DiskHot = v));
        heat.Items.Add(new Separator());
        heat.Items.Add(Toggle("Notification si trop chaud plus de 30 s", s.HeatAlerts, v => s.HeatAlerts = v));
        m.Items.Add(heat);
        m.Items.Add(new Separator());

        m.Items.Add(Choice("Premier plan", !s.Topmost ? 0 : s.TopmostForce ? 2 : 1, v => { s.Topmost = v > 0; s.TopmostForce = v == 2; },
            ("Normal", 0), ("Toujours au premier plan", 1), ("Au-dessus de tout, quoi qu'il arrive", 2)));
        m.Items.Add(Toggle("Clics traversants (maintiens Ctrl pour le saisir)", s.ClickThrough, v => s.ClickThrough = v));
        m.Items.Add(Toggle("Verrouiller la position", s.Locked, v => s.Locked = v));
        m.Items.Add(Choice("Raccourci afficher / masquer", s.Hotkey, v => s.Hotkey = v,
            ("Aucun", ""), ("Ctrl+Alt+T", "Ctrl+Alt+T"), ("Ctrl+Maj+T", "Ctrl+Shift+T"), ("Ctrl+Alt+H", "Ctrl+Alt+H")));
        m.Items.Add(Item("Replacer en haut à droite", () => widget.ResetPosition()));
        m.Items.Add(new Separator());

        var updates = Choice("Mises à jour", s.UpdateMode, v => s.UpdateMode = v,
            ("Automatiques", Updater.Auto), ("Me prévenir seulement", Updater.NotifyOnly), ("Désactivées", Updater.Off));
        updates.Items.Add(new Separator());
        updates.Items.Add(Item("Rechercher maintenant", () => _ = updater.Check(manual: true)));
        updates.Items.Add(new MenuItem { Header = $"Version installée : {Version}", IsEnabled = false });
        m.Items.Add(updates);
        m.Items.Add(Item("Passer en mode simple", () => SetMode(AppSettings.Lite)));
        m.Items.Add(Item("Licence et conditions d'utilisation", OpenLicense));
        m.Items.Add(Item(widget.IsVisible ? "Masquer le widget" : "Afficher le widget", ToggleWidget));
        m.Items.Add(Item("Quitter Temperator", Quit));
        return m;
    }

    /// <summary>Sous-menu de curseurs : le widget change en direct, enregistré à la fermeture du menu.</summary>
    static MenuItem Sliders(string text, params MenuItem[] rows)
    {
        var sub = new MenuItem { Header = text };
        foreach (var r in rows) sub.Items.Add(r);
        return sub;
    }

    MenuItem Percent(string name, double value, double min, Action<double> set) =>
        SliderRow(name, value, min, 1, 0.05, v => $"{v * 100:0} %", set);

    MenuItem Degrees(string name, double value, double min, double max, Action<double> set) =>
        SliderRow(name, value, min, max, 1, v => Settings.Fahrenheit ? $"{Fmt.Temp((float)v, true):0} °F" : $"{v:0} °C", v => set(Math.Round(v)));

    MenuItem SliderRow(string name, double value, double min, double max, double step, Func<double, string> format, Action<double> set)
    {
        var label = new TextBlock { Width = 50, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Soft, Text = format(value) };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value, Width = 170, IsMoveToPointEnabled = true,
            SmallChange = step, LargeChange = step * 5, VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            label.Text = format(e.NewValue);
            set(e.NewValue);
            widget.ApplySettings();
            widget.Apply(Last);
        };
        var row = new DockPanel { Margin = new Thickness(-14, 2, 0, 2) };
        DockPanel.SetDock(label, Dock.Right);
        row.Children.Add(label);
        if (name.Length > 0)
        {
            var title = new TextBlock { Text = name, Width = 52, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Muted };
            DockPanel.SetDock(title, Dock.Left);
            row.Children.Add(title);
        }
        row.Children.Add(slider);
        return new MenuItem { Header = row, StaysOpenOnClick = true };
    }

    /// <summary>La licence installée avec l'application (Bloc-notes), sinon celle du dépôt GitHub.</summary>
    static void OpenLicense()
    {
        try
        {
            string file = Path.Combine(AppContext.BaseDirectory, "LICENSE");
            if (File.Exists(file)) System.Diagnostics.Process.Start("notepad.exe", "\"" + file + "\"");
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://github.com/{Updater.Repo}/blob/main/LICENSE") { UseShellExecute = true });
        }
        catch (Exception ex) { Log(ex); }
    }

    static MenuItem Item(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        return item;
    }

    MenuItem Toggle(string text, bool value, Action<bool> set)
    {
        var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = value };
        item.Click += (_, _) => { set(item.IsChecked); Changed(); };
        return item;
    }

    MenuItem Choice<T>(string text, T current, Action<T> set, params (string Label, T Value)[] options)
    {
        var sub = new MenuItem { Header = text };
        foreach (var (label, value) in options)
        {
            var item = new MenuItem { Header = label, IsChecked = Equals(value, current) };
            item.Click += (_, _) => { set(value); Changed(); };
            sub.Items.Add(item);
        }
        return sub;
    }

    // ------------------------------------------------------------------ journal d'erreurs

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try { File.AppendAllText(Path.Combine(AppSettings.Folder, "erreurs.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\r\n\r\n"); }
        catch { }
    }

    // ------------------------------------------------------------------ captures d'écran (développement / README)

    async Task Snapshot(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Settings = new AppSettings { ShowFan = true, Gauges = false };
            Hub = new SensorHub(Dispatcher);
            updater = new Updater(this);
            if (Demo) History.FillDemo();
            widget = new MainWindow(this) { Left = -20000, Top = -20000 };
            var ready = new TaskCompletionSource();
            int n = 0;
            bool frozen = false;
            Hub.Updated += r =>
            {
                if (frozen) return;
                Last = r;
                widget.Apply(r);
                if (r.Ready && ++n == 3) ready.TrySetResult();
            };
            await Task.WhenAny(ready.Task, Task.Delay(40000));
            if (Demo)
            {
                frozen = true;
                Last = new Readings
                {
                    Ready = true, CpuName = Last.CpuName, CpuTemp = 58, CpuLoad = 23, CpuPower = 21.5f,
                    GpuName = Last.GpuName ?? "Carte graphique", GpuTemp = 64, GpuLoad = 41,
                    RamUsed = 11.4f, RamTotal = 15.7f, RamLoad = 72, DiskTemp = 41, DiskLoad = 6, DiskName = "SSD", FanRpm = 1850,
                };
                widget.Apply(Last);   // les capteurs continuent (pour le plan du PC), mais le widget garde les valeurs d'exemple
            }

            widget.Show();
            widget.Left = widget.Top = -20000;
            map = new MapWindow(this) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            map.Show();
            map.Refresh();
            await Task.Delay(1200);   // fin des animations

            SavePng(widget, Path.Combine(dir, "widget.png"));
            SavePng(map, Path.Combine(dir, "plan-du-pc.png"));

            if (Demo)
            {
                var normal = Last;
                // trop chaud : flammes
                widget.Apply(new Readings
                {
                    Ready = true, CpuName = normal.CpuName, CpuTemp = 94, CpuLoad = 100, GpuName = normal.GpuName, GpuTemp = 79, GpuLoad = 97,
                    RamUsed = 13.9f, RamTotal = 15.7f, RamLoad = 88, DiskTemp = 47, DiskLoad = 35, FanRpm = 4200,
                });
                await Task.Delay(1600);
                SavePng(widget, Path.Combine(dir, "widget-feu.png"));

                // translucide, posé sur un fond sombre puis sur un fond clair
                Settings.Translucent = true;
                widget.ApplySettings();
                widget.Apply(normal);
                await Task.Delay(2500);   // les flammes s'éteignent
                foreach (var (name, a, b) in new[]
                {
                    ("widget-translucide-sombre.png", Color.FromRgb(0x0F, 0x20, 0x27), Color.FromRgb(0x2C, 0x53, 0x64)),
                    ("widget-translucide-clair.png", Color.FromRgb(0xF5, 0xF7, 0xFA), Color.FromRgb(0xB8, 0xC6, 0xDB)),
                    ("widget-transparent-sombre.png", Color.FromRgb(0x0F, 0x20, 0x27), Color.FromRgb(0x2C, 0x53, 0x64)),
                    ("widget-transparent-clair.png", Color.FromRgb(0xF5, 0xF7, 0xFA), Color.FromRgb(0xB8, 0xC6, 0xDB)),
                })
                {
                    if (name.Contains("transparent") && !Settings.Transparent)
                    {
                        Settings.Transparent = true;   // puis sans aucun fond
                        widget.ApplySettings();
                    }
                    widget.ApplyBackdrop(Backdrop.Of(Palette.Mix(a, b, 0.5)), animate: false);
                    await Task.Delay(300);
                    File.AppendAllText(Path.Combine(dir, "lectures.txt"), $"{name}: fond={Palette.Mix(a, b, 0.5)} luma={Backdrop.Of(Palette.Mix(a, b, 0.5)).Luma:0.00} palette claire={widget.LightPalette}\n");
                    SavePng(widget, Path.Combine(dir, name), new LinearGradientBrush(a, b, 35));
                }
            }

            if (Demo)
            {
                var normal = Last;
                Settings.Transparent = Settings.Translucent = false;
                Settings.Gauges = true;
                widget.ApplySettings();
                widget.Apply(normal);
                await Task.Delay(900);
                SavePng(widget, Path.Combine(dir, "widget-jauges.png"));

                widget.Apply(new Readings
                {
                    Ready = true, CpuName = normal.CpuName, CpuTemp = 94, CpuLoad = 100, GpuName = normal.GpuName, GpuTemp = 79, GpuLoad = 97,
                    RamUsed = 13.9f, RamTotal = 15.7f, RamLoad = 88, DiskTemp = 47, DiskLoad = 35,
                });
                await Task.Delay(1600);
                SavePng(widget, Path.Combine(dir, "widget-jauges-feu.png"));

                widget.Apply(normal);
                await Task.Delay(2500);
                Settings.BackgroundOpacity = 0.4;
                widget.ApplySettings();
                var (a, b) = (Color.FromRgb(0x0F, 0x20, 0x27), Color.FromRgb(0x2C, 0x53, 0x64));
                widget.ApplyBackdrop(Backdrop.Of(Palette.Mix(a, b, 0.5)), animate: false);
                await Task.Delay(300);
                SavePng(widget, Path.Combine(dir, "widget-jauges-fond-40.png"), new LinearGradientBrush(a, b, 35));
            }

            if (Demo)   // fenêtre de bienvenue (choix du mode)
            {
                var w = new WelcomeWindow(AppSettings.Lite) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Topmost = false };
                w.Show();
                await Task.Delay(600);
                SavePng(w, Path.Combine(dir, "bienvenue.png"));
                w.Close();
            }

            if (Demo)   // infobulle avec la courbe des 5 dernières minutes
            {
                var tip = new HistoryTip(History, History.CpuTemp, true, () => false, () => Settings.CpuHot) { Text = "11th Gen Intel Core i5-11400H\nConsommation : 21,5 W" };
                var bubble = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x24)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xF1, 0xF3, 0xF6)),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Child = tip,
                };
                TextElement.SetForeground(bubble, Theme.Text);
                TextElement.SetFontSize(bubble, 12);
                TextElement.SetFontFamily(bubble, new FontFamily("Segoe UI"));
                var host = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
                    SizeToContent = SizeToContent.WidthAndHeight, Left = -20000, Top = -20000, Content = bubble, ShowActivated = false,
                };
                host.Show();
                await Task.Delay(400);
                SavePng(host, Path.Combine(dir, "infobulle-historique.png"));
                host.Close();
            }

            // test du lecteur d'écran sur une zone réelle
            var probe = Backdrop.Around(new System.Drawing.Rectangle(400, 300, 270, 160));
            File.AppendAllText(Path.Combine(dir, "lectures.txt"), probe is { } pr
                ? $"fond: couleur={pr.Average} luma={pr.Luma:0.00} agitation={pr.Busy:0.00}\n" : "fond: illisible\n");
            File.AppendAllText(Path.Combine(dir, "lectures.txt"),
                $"admin={IsAdmin}\ncpu={Last.CpuName} {Last.CpuTemp} °C {Last.CpuLoad} %\ngpu={Last.GpuName} {Last.GpuTemp} °C {Last.GpuLoad} %\n" +
                $"ram={Last.RamUsed}/{Last.RamTotal} Go {Last.RamLoad} %\ndisque={Last.DiskName} {Last.DiskTemp} °C {Last.DiskLoad} %\nventilo={Last.FanRpm}\n");
        }
        catch (Exception ex) { Log(ex); File.WriteAllText(Path.Combine(dir, "erreur.txt"), ex.ToString()); }
        Hub?.Dispose();
        Shutdown();
    }

    static void SavePng(Window w, string path, Brush? background = null)
    {
        const double k = 2;
        double width = w.ActualWidth, height = w.ActualHeight;
        var rtb = new RenderTargetBitmap((int)(width * k), (int)(height * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
        if (background != null)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            rtb.Render(dv);
        }
        rtb.Render(w);   // rendu direct (un VisualBrush garderait une image périmée après un changement de couleurs)
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
