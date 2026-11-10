using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            widget.Apply(r);
            map?.Refresh();
            tray?.Update(r, Settings);
        };
        // toujours visible au lancement (masqué = seulement pour la session en cours)
        Settings.Hidden = false;
        widget.Show();
        tray = new Tray(this);
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
        menu.Opened += (_, _) => widget.PauseTopGuard(true);
        menu.Closed += (_, _) => widget.PauseTopGuard(false);
        menu.Placement = PlacementMode.MousePoint;
        if (target != null) menu.PlacementTarget = target;
        else Native.SetForegroundWindow(new WindowInteropHelper(widget).Handle);   // pour que le menu se ferme en cliquant ailleurs
        menu.IsOpen = true;
    }

    ContextMenu BuildMenu()
    {
        var s = Settings;
        var m = new ContextMenu();

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

        m.Items.Add(Choice("Taille", s.Scale, v => s.Scale = v,
            ("Petite", 0.85), ("Normale", 1.0), ("Grande", 1.2), ("Très grande", 1.45)));
        m.Items.Add(Choice("Opacité", s.Opacity, v => s.Opacity = v,
            ("100 %", 1.0), ("90 %", 0.9), ("75 %", 0.75), ("60 %", 0.6)));
        m.Items.Add(Choice("Actualisation", s.IntervalMs, v => s.IntervalMs = v,
            ("Toutes les 0,5 s", 500), ("Chaque seconde", 1000), ("Toutes les 2 s", 2000), ("Toutes les 5 s", 5000)));
        m.Items.Add(Toggle("Degrés Fahrenheit", s.Fahrenheit, v => s.Fahrenheit = v));
        m.Items.Add(new Separator());

        m.Items.Add(Choice("Fond", s.Transparent ? 2 : s.Translucent ? 1 : 0, v => { s.Translucent = v == 1; s.Transparent = v == 2; },
            ("Opaque", 0), ("Translucide (s'adapte au fond)", 1), ("Aucun fond, 100 % transparent", 2)));
        m.Items.Add(Toggle("Flammes quand c'est trop chaud", s.FireAnimation, v => s.FireAnimation = v));
        m.Items.Add(new Separator());

        m.Items.Add(Choice("Premier plan", !s.Topmost ? 0 : s.TopmostForce ? 2 : 1, v => { s.Topmost = v > 0; s.TopmostForce = v == 2; },
            ("Normal", 0), ("Toujours au premier plan", 1), ("Au-dessus de tout, quoi qu'il arrive", 2)));
        m.Items.Add(Toggle("Verrouiller la position", s.Locked, v => s.Locked = v));
        m.Items.Add(Item("Replacer en haut à droite", () => widget.ResetPosition()));
        m.Items.Add(new Separator());

        m.Items.Add(Item(widget.IsVisible ? "Masquer le widget" : "Afficher le widget", ToggleWidget));
        m.Items.Add(Item("Quitter Temperator", Quit));
        return m;
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
            Settings = new AppSettings { ShowFan = true };
            Hub = new SensorHub(Dispatcher);
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
                Hub.Dispose();
                widget.Apply(Last);
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
