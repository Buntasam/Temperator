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

        if (e.Args.Length >= 2 && e.Args[0] == "--fps-test") { _ = FpsTest(e.Args[1]); return; }
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

    // ------------------------------------------------------------------ journal d'erreurs

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try { File.AppendAllText(Path.Combine(AppSettings.Folder, "erreurs.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\r\n\r\n"); }
        catch { }
    }

}
