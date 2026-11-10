using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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

    /// <summary>Historique : 10 minutes en détail, 24 heures en moyennes (infobulles, plan du PC, export CSV).</summary>
    internal History History { get; } = new();
    Updater updater = null!;
    readonly Dictionary<string, DateTime> hotSince = new(), lastAlert = new();

    MainWindow widget = null!;
    MapWindow? map;
    SettingsWindow? settingsWindow;
    WelcomeWindow? welcome;
    Tray? tray;
    Mutex? instance;
    EventWaitHandle? showSignal, quitSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);

        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            Snapshotting = true;
            Demo = e.Args.Contains("--demo");
            L.Init(e.Args.Contains("--en") ? L.English : L.French);
            _ = Snapshot(e.Args[1]);
            return;
        }

        // une seule instance : la deuxième demande à la première de se montrer, puis s'arrête
        instance = new Mutex(true, @"Global\Temperator.Instance", out bool first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(@"Global\Temperator.Show", out var show)) show.Set();
            Shutdown();
            return;
        }

        Settings = AppSettings.Load();
        L.Init(Settings.Language);
        History.Load(AppSettings.Folder);
        showSignal = Listen(@"Global\Temperator.Show", () => SetHidden(false, flash: true));
        quitSignal = Listen(@"Global\Temperator.Quit", Quit);   // utilisé par l'installeur pour fermer proprement

        Hub = new SensorHub(Dispatcher) { IntervalMs = Settings.IntervalMs };
        PassHardwareChoice();
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
            Notify(L.T($"Temperator mis à jour en {Version}", $"Temperator updated to {Version}"),
                   L.T("Clic droit sur le widget pour voir les réglages.", "Right-click the widget to see the settings."));
        Settings.LastRunVersion = Version;
        Settings.Save();
        updater = new Updater(this);
        updater.Start();

        if (Settings.Mode is not (AppSettings.Lite or AppSettings.Full)) ShowWelcome();
    }

    void PassHardwareChoice()
    {
        Hub.GpuId = Settings.GpuId;
        Hub.DiskId = Settings.DiskId;
        Hub.NetId = Settings.NetId;
    }

    // ------------------------------------------------------------------ mode simple ou complet

    /// <summary>Fenêtre de bienvenue : choisir entre le mode simple et le mode complet.</summary>
    public void ShowWelcome()
    {
        if (welcome != null) { welcome.Activate(); return; }
        welcome = new WelcomeWindow(Settings.Mode ?? AppSettings.Lite);
        welcome.Closed += (_, _) =>
        {
            SetMode(welcome!.Choice, announce: false);
            welcome = null;
            Notify(Settings.IsLite ? L.T("Mode simple", "Simple mode") : L.T("Mode complet", "Full mode"),
                Settings.IsLite ? L.T("Clic droit sur le widget pour le menu. Tu peux passer en mode complet à tout moment.",
                                      "Right-click the widget for the menu. You can switch to full mode at any time.")
                                : L.T("Clic droit sur le widget, puis Réglages, pour toutes les options.",
                                      "Right-click the widget, then Settings, for every option."));
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
            Notify(Settings.IsLite ? L.T("Mode simple", "Simple mode") : L.T("Mode complet", "Full mode"),
                Settings.IsLite ? L.T("Un menu court, l'essentiel seulement.", "A short menu, just the essentials.")
                                : L.T("Clic droit sur le widget, puis Réglages, pour toutes les options.", "Right-click the widget, then Settings, for every option."));
    }

    /// <summary>Notification Windows (zone de notification) ; un clic dessus lance onClick.</summary>
    public void Notify(string title, string message, bool warning = false, Action? onClick = null) =>
        tray?.Notify(title, message, warning, onClick);

    // ------------------------------------------------------------------ alertes : trop chaud pendant plus de 30 s (même widget masqué)

    void CheckHeat(Readings r)
    {
        if (!Settings.HeatAlerts || !r.Ready) return;
        Heat("CPU", L.T("Processeur", "Processor"), r.CpuTemp, Settings.CpuHot);
        Heat("GPU", L.T("Carte graphique", "Graphics card"), r.GpuTemp, Settings.GpuHot);
        Heat("DISK", L.T("Disque", "Drive"), r.DiskTemp, Settings.DiskHot);
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
        string unit = f ? "°F" : "°C", now_ = $"{Fmt.Temp(c, f):0} {unit}", lim = $"{Fmt.Temp((float)limit, f):0} {unit}";
        Notify(L.T($"{name} trop chaud : {now_}", $"{name} too hot: {now_}"),
            L.T($"Au-dessus de {lim} depuis plus de 30 secondes. Vérifie la ventilation et ce qui tourne. Clique pour ouvrir le plan du PC.",
                $"Above {lim} for more than 30 seconds. Check the cooling and what's running. Click to open the PC map."),
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

    public void ShowSettings(string page = "look")
    {
        if (settingsWindow == null)
        {
            settingsWindow = new SettingsWindow(this, page);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }
        else
        {
            settingsWindow.ShowPage(page);
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
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

    public void ResetWidgetPosition() => widget.ResetPosition();

    public void CheckForUpdates() => _ = updater.Check(manual: true);

    /// <summary>Un réglage a changé : on l'enregistre et on l'applique partout.</summary>
    public void Changed()
    {
        Settings.Save();
        ApplyLive();
    }

    /// <summary>Applique les réglages sans les enregistrer (curseurs en cours de déplacement).</summary>
    public void ApplyLive()
    {
        Hub.IntervalMs = Settings.IntervalMs;
        PassHardwareChoice();
        widget.ApplySettings();
        widget.Apply(Last);
        map?.Refresh();
    }

    /// <summary>Relance Temperator (changement de langue) : une nouvelle instance démarre 2 secondes après.</summary>
    public void Restart()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Temperator.exe");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & start \"\" \"{exe}\"") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex) { Log(ex); }
        Quit();
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

    // ------------------------------------------------------------------ outils

    public void ExportHistory(Window? owner)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"temperator-{L.T("historique", "history")}-{DateTime.Now:yyyy-MM-dd-HHmm}.csv",
            Filter = L.T("Fichier CSV (*.csv)|*.csv", "CSV file (*.csv)|*.csv"),
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            History.ExportCsv(dialog.FileName);
            Notify(L.T("Historique exporté", "History exported"), dialog.FileName, onClick: () => OpenUrl(Path.GetDirectoryName(dialog.FileName)!));
        }
        catch (Exception ex)
        {
            Log(ex);
            Notify(L.T("Export impossible", "Export failed"), ex.Message, warning: true);
        }
    }

    /// <summary>Version, Windows, matériel, valeurs, réglages et dernières erreurs : à coller dans une issue GitHub.</summary>
    public void CopyDiagnostics()
    {
        var r = Last;
        string V(float? v, string unit) => v is float x ? $"{x:0.#}{unit}" : "—";
        var sb = new StringBuilder();
        sb.AppendLine($"Temperator {Version} — diagnostic {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Windows : {Fmt.OsName()} · .NET {Environment.Version} · admin={IsAdmin} · PawnIO={Native.PawnIOInstalled.Value}");
        sb.AppendLine($"Langue={Settings.Language} ({(L.Fr ? "fr" : "en")}) · mode={Settings.Mode} · affichage={(Settings.Gauges ? "jauges" : "lignes")}");
        sb.AppendLine();
        sb.AppendLine($"CPU : {r.CpuName} · {V(r.CpuTemp, " °C")} · {V(r.CpuLoad, " %")} · {V(r.CpuPower, " W")}");
        sb.AppendLine($"GPU : {r.GpuName} · {V(r.GpuTemp, " °C")} · {V(r.GpuLoad, " %")} · {V(r.GpuPower, " W")}");
        foreach (var (_, name) in r.Gpus) sb.AppendLine($"  GPU détecté : {name}");
        sb.AppendLine($"RAM : {V(r.RamUsed, " Go")} / {V(r.RamTotal, " Go")} · {V(r.RamLoad, " %")}");
        sb.AppendLine($"Disque : {r.DiskName} · {V(r.DiskTemp, " °C")} · {V(r.DiskLoad, " %")}");
        foreach (var (_, name) in r.Disks) sb.AppendLine($"  disque détecté : {name}");
        sb.AppendLine($"Réseau : {r.NetName} · ↓ {V(r.NetDown, " o/s")} · ↑ {V(r.NetUp, " o/s")} ({r.Nets.Count} cartes)");
        sb.AppendLine($"Batterie : {V(r.BatteryLevel, " %")} · {V(r.BatteryRate, " W")} · charge={r.BatteryCharging}");
        sb.AppendLine($"Ventilateur : {V(r.FanRpm, " tr/min")} · conso CPU+GPU : {V(r.TotalPower, " W")}");
        sb.AppendLine();
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings))!;
        copy.Left = copy.Top = null;
        sb.AppendLine("Réglages : " + JsonSerializer.Serialize(copy));
        try
        {
            string log = Path.Combine(AppSettings.Folder, "erreurs.log");
            if (File.Exists(log))
            {
                sb.AppendLine();
                sb.AppendLine("Dernières erreurs :");
                foreach (var line in File.ReadLines(log).TakeLast(40)) sb.AppendLine(line);
            }
        }
        catch { }

        for (int i = 0; i < 5; i++)   // presse-papiers parfois occupé par une autre application
        {
            try
            {
                Clipboard.SetText(sb.ToString());
                Notify(L.T("Rapport copié", "Report copied"), L.T("Colle-le dans une issue sur GitHub (Ctrl+V).", "Paste it into an issue on GitHub (Ctrl+V)."));
                return;
            }
            catch { Thread.Sleep(100); }
        }
        Notify(L.T("Copie impossible", "Copy failed"), L.T("Le presse-papiers est occupé. Réessaie.", "The clipboard is busy. Try again."), warning: true);
    }

    /// <summary>La licence installée avec l'application (Bloc-notes), sinon celle du dépôt GitHub.</summary>
    internal static void OpenLicense()
    {
        try
        {
            string file = Path.Combine(AppContext.BaseDirectory, "LICENSE");
            if (File.Exists(file)) Process.Start("notepad.exe", "\"" + file + "\"");
            else OpenUrl($"https://github.com/{Updater.Repo}/blob/main/LICENSE");
        }
        catch (Exception ex) { Log(ex); }
    }

    internal static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log(ex); }
    }

    // ------------------------------------------------------------------ menu (clic droit sur le widget ou l'icône)

    public void ShowMenu(UIElement? target)
    {
        var menu = BuildMenu();
        menu.Opened += (_, _) => widget.MenuOpen(true);
        menu.Closed += (_, _) => widget.MenuOpen(false);
        menu.Placement = PlacementMode.MousePoint;
        if (target != null) menu.PlacementTarget = target;
        else Native.SetForegroundWindow(new WindowInteropHelper(widget).Handle);   // pour que le menu se ferme en cliquant ailleurs
        menu.IsOpen = true;
    }

    ContextMenu BuildMenu()
    {
        var s = Settings;
        var m = new ContextMenu();
        if (updater.Available is string next)
        {
            var up = Item(L.T($"Mettre à jour vers la version {next}", $"Update to version {next}"), () => _ = updater.Install());
            up.FontWeight = FontWeights.SemiBold;
            up.Foreground = Theme.Accent;
            m.Items.Add(up);
        }
        var plan = Item(L.T("Plan du PC…", "PC map…"), ShowMap);
        plan.FontWeight = FontWeights.SemiBold;
        m.Items.Add(plan);
        if (!s.IsLite) m.Items.Add(Item(L.T("Réglages…", "Settings…"), () => ShowSettings()));
        m.Items.Add(new Separator());

        m.Items.Add(Choice(L.T("Affichage", "Layout"), s.Gauges, v => s.Gauges = v,
            (L.T("Jauges rondes", "Round gauges"), true), (L.T("Lignes et barres", "Rows and bars"), false)));
        if (s.IsLite)
        {
            // mode simple : l'essentiel, rien de plus
            m.Items.Add(Choice(L.T("Taille", "Size"), s.Scale, v => s.Scale = v,
                (L.T("Petite", "Small"), 0.85), (L.T("Normale", "Normal"), 1.0), (L.T("Grande", "Large"), 1.2), (L.T("Très grande", "Extra large"), 1.45)));
            m.Items.Add(Toggle(L.T("Toujours au premier plan", "Always on top"), s.Topmost, v => { s.Topmost = v; s.TopmostForce = false; }));
            m.Items.Add(Item(L.T("Replacer en haut à droite", "Move back to top right"), () => widget.ResetPosition()));
            m.Items.Add(new Separator());
            var full = Item(L.T("Mode complet : toutes les options…", "Full mode: every option…"), () => { SetMode(AppSettings.Full); ShowSettings(); });
            full.Foreground = Theme.Accent;
            m.Items.Add(full);
        }
        else
        {
            m.Items.Add(Choice(L.T("Fond", "Background"), s.Transparent ? 2 : s.Translucent ? 1 : 0, v => { s.Translucent = v == 1; s.Transparent = v == 2; },
                (L.T("Opaque", "Opaque"), 0), (L.T("Translucide", "Translucent"), 1), (L.T("Aucun fond", "None"), 2)));
            m.Items.Add(new Separator());
        }
        m.Items.Add(Item(widget.IsVisible ? L.T("Masquer le widget", "Hide the widget") : L.T("Afficher le widget", "Show the widget"), ToggleWidget));
        m.Items.Add(Item(L.T("Quitter Temperator", "Quit Temperator"), Quit));
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
            Settings = new AppSettings { ShowFan = true, Gauges = false, Mode = AppSettings.Full };
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
                var real = Last;
                Last = new Readings
                {
                    Ready = true, CpuName = real.CpuName, CpuTemp = 58, CpuLoad = 23, CpuPower = 21.5f,
                    GpuName = real.GpuName ?? "GPU", GpuTemp = 64, GpuLoad = 41, GpuPower = 38,
                    RamUsed = 11.4f, RamTotal = 15.7f, RamLoad = 72, DiskTemp = 41, DiskLoad = 6, DiskName = "SSD", FanRpm = 1850,
                    TotalPower = 59.5f, NetDown = 12.3e6f, NetUp = 1.2e6f, NetName = "Wi-Fi", BatteryLevel = 78, BatteryRate = -9,
                    Gpus = real.Gpus, Disks = real.Disks, Nets = real.Nets,
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
                widget.Apply(Hot(normal, fan: true));
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
                    SavePng(widget, Path.Combine(dir, name), new LinearGradientBrush(a, b, 35));
                }

                Settings.Transparent = Settings.Translucent = false;
                Settings.Gauges = true;
                widget.ApplySettings();
                widget.Apply(normal);
                await Task.Delay(900);
                SavePng(widget, Path.Combine(dir, "widget-jauges.png"));

                widget.Apply(Hot(normal, fan: false));
                await Task.Delay(1600);
                SavePng(widget, Path.Combine(dir, "widget-jauges-feu.png"));

                widget.Apply(normal);
                await Task.Delay(2500);
                Settings.BackgroundOpacity = 0.4;
                widget.ApplySettings();
                var (c1, c2) = (Color.FromRgb(0x0F, 0x20, 0x27), Color.FromRgb(0x2C, 0x53, 0x64));
                widget.ApplyBackdrop(Backdrop.Of(Palette.Mix(c1, c2, 0.5)), animate: false);
                await Task.Delay(300);
                SavePng(widget, Path.Combine(dir, "widget-jauges-fond-40.png"), new LinearGradientBrush(c1, c2, 35));

                // toutes les informations (réseau, batterie, conso), en jauges puis en lignes, couleur d'accent violette
                Settings.BackgroundOpacity = 1;
                Settings.ShowNet = Settings.ShowBattery = Settings.ShowPower = true;
                Settings.Accent = "#A78BFA";
                widget.ApplySettings();
                widget.Apply(normal);
                await Task.Delay(900);
                SavePng(widget, Path.Combine(dir, "widget-jauges-tout.png"));
                Settings.Gauges = false;
                widget.ApplySettings();
                widget.Apply(normal);
                await Task.Delay(900);
                SavePng(widget, Path.Combine(dir, "widget-lignes-tout.png"));
                Settings.Accent = "#4ED8C0";
                Settings.Gauges = true;
                Settings.ShowNet = Settings.ShowPower = false;
                widget.ApplySettings();
                widget.Apply(normal);

                // fenêtre de bienvenue (choix du mode)
                var w = new WelcomeWindow(AppSettings.Lite) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Topmost = false };
                w.Show();
                await Task.Delay(600);
                SavePng(w, Path.Combine(dir, "bienvenue.png"));
                w.Close();

                // réglages : chaque onglet
                var sw = new SettingsWindow(this) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
                sw.Show();
                foreach (var (key, _, _, _) in SettingsWindow.Pages)
                {
                    sw.ShowPage(key);
                    await Task.Delay(350);
                    SavePng(sw, Path.Combine(dir, $"reglages-{key}.png"));
                }
                sw.Close();

                // infobulle avec la courbe des 5 dernières minutes
                var tip = new HistoryTip(History, History.CpuTemp, true, () => false, () => Settings.CpuHot) { Text = $"{normal.CpuName}\n{L.T("Consommation", "Power")} : 21,5 W" };
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

                // plan du PC sur 24 heures
                map.ShowSpan(History.LongKeep);
                await Task.Delay(400);
                SavePng(map, Path.Combine(dir, "plan-du-pc-24h.png"));
            }

            File.AppendAllText(Path.Combine(dir, "lectures.txt"),
                $"admin={IsAdmin}\ncpu={Last.CpuName} {Last.CpuTemp} °C {Last.CpuLoad} %\ngpu={Last.GpuName} {Last.GpuTemp} °C {Last.GpuLoad} % {Last.GpuPower} W\n" +
                $"ram={Last.RamUsed}/{Last.RamTotal} {Last.RamLoad} %\ndisque={Last.DiskName} {Last.DiskTemp} °C {Last.DiskLoad} %\nventilo={Last.FanRpm}\n" +
                $"réseau={Last.NetName} ↓{Last.NetDown} ↑{Last.NetUp} ({Last.Nets.Count})\nbatterie={Last.BatteryLevel} {Last.BatteryRate} W charge={Last.BatteryCharging}\n" +
                $"gpus={string.Join(" | ", Last.Gpus.Select(g => g.Name))}\ndisques={string.Join(" | ", Last.Disks.Select(d => d.Name))}\n");
        }
        catch (Exception ex) { Log(ex); File.WriteAllText(Path.Combine(dir, "erreur.txt"), ex.ToString()); }
        Hub?.Dispose();
        Shutdown();
    }

    static Readings Hot(Readings r, bool fan) => new()
    {
        Ready = true, CpuName = r.CpuName, CpuTemp = 94, CpuLoad = 100, CpuPower = 65, GpuName = r.GpuName, GpuTemp = 79, GpuLoad = 97, GpuPower = 80,
        RamUsed = 13.9f, RamTotal = 15.7f, RamLoad = 88, DiskTemp = 47, DiskLoad = 35, FanRpm = fan ? 4200 : null, TotalPower = 145,
    };

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
