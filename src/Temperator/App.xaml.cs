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

        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot") { Demo = Array.IndexOf(e.Args, "--demo") > 0; _ = Snapshot(e.Args[1]); return; }

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
        if (!Settings.Hidden) widget.Show();
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

        m.Items.Add(Toggle("Toujours au premier plan", s.Topmost, v => s.Topmost = v));
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
            File.WriteAllText(Path.Combine(dir, "lectures.txt"),
                $"admin={IsAdmin}\ncpu={Last.CpuName} {Last.CpuTemp} °C {Last.CpuLoad} %\ngpu={Last.GpuName} {Last.GpuTemp} °C {Last.GpuLoad} %\n" +
                $"ram={Last.RamUsed}/{Last.RamTotal} Go {Last.RamLoad} %\ndisque={Last.DiskName} {Last.DiskTemp} °C {Last.DiskLoad} %\nventilo={Last.FanRpm}\n");
        }
        catch (Exception ex) { Log(ex); File.WriteAllText(Path.Combine(dir, "erreur.txt"), ex.ToString()); }
        Hub?.Dispose();
        Shutdown();
    }

    static void SavePng(Window w, string path)
    {
        const double k = 2;
        double width = w.ActualWidth, height = w.ActualHeight;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(w), null, new Rect(0, 0, width, height));
        var rtb = new RenderTargetBitmap((int)(width * k), (int)(height * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
