using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace Temperator;

/// <summary>Le widget, en haut à droite de l'écran.</summary>
public partial class MainWindow : Window
{
    readonly App app;
    readonly MetricRow cpu = new("CPU"), gpu = new("GPU"), ram = new("RAM"), disk = new("DISQUE"), fan = new("VENTILO");
    int updates;

    // largeur estimée avant le premier affichage, pour éviter un saut de position
    const double EstimatedWidth = 218 + 28 + 2 + 24;

    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();

        foreach (var row in new[] { cpu, gpu, ram, disk, fan }) Rows.Children.Add(row.Root);
        fan.Wide();
        gpu.Visible = disk.Visible = fan.Visible = false;   // affichés dès qu'on sait qu'ils existent
        cpu.Visible = app.Settings.ShowCpu;
        ram.Visible = app.Settings.ShowRam;

        var s = app.Settings;
        if (s.Left is double l && s.Top is double t) { Left = l; Top = t; }
        else { Left = SystemParameters.WorkArea.Right - EstimatedWidth + 4; Top = SystemParameters.WorkArea.Top - 4; }
        ApplySettings();

        SourceInitialized += (_, _) => Native.MakeToolWindow(this);
        Loaded += (_, _) => EnsureOnScreen();
        SizeChanged += (_, _) => { if (app.Settings.Left == null) PlaceDefault(); };
        Root.MouseLeftButtonDown += OnLeftDown;
        Root.MouseRightButtonUp += (_, e) => { app.ShowMenu(Root); e.Handled = true; };
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(EnsureOnScreen);
    }

    // ------------------------------------------------------------------ position

    void PlaceDefault()
    {
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
        try { DragMove(); } catch (InvalidOperationException) { }
        if (Math.Abs(Left - l) > 1 || Math.Abs(Top - t) > 1)
        {
            app.Settings.Left = Left;
            app.Settings.Top = Top;
            app.Settings.Save();
        }
    }

    // ------------------------------------------------------------------ affichage

    public void ApplySettings()
    {
        var s = app.Settings;
        Topmost = s.Topmost;
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = s.Opacity;
        Zoom.ScaleX = Zoom.ScaleY = s.Scale;
    }

    /// <summary>Petit clignotement pour montrer où est le widget.</summary>
    public void Flash()
    {
        var a = new DoubleAnimation(0.25, app.Settings.Opacity, TimeSpan.FromMilliseconds(320)) { AutoReverse = false, RepeatBehavior = new RepeatBehavior(2) };
        a.Completed += (_, _) => ApplySettings();
        Root.BeginAnimation(OpacityProperty, a);
        Activate();
    }

    public void Apply(Readings r)
    {
        if (!r.Ready) return;
        var s = app.Settings;
        bool f = s.Fahrenheit;

        cpu.Visible = s.ShowCpu;
        SetTemp(cpu, r.CpuTemp, f, 70, 85);
        cpu.SetLoad(r.CpuLoad);
        cpu.Root.ToolTip = Lines(r.CpuName, r.CpuPower is float p ? $"Consommation : {p.ToString("0.#", Fmt.Fr)} W" : null);

        gpu.Visible = s.ShowGpu && r.GpuName != null;
        SetTemp(gpu, r.GpuTemp, f, 75, 87);
        gpu.SetLoad(r.GpuLoad);
        gpu.Root.ToolTip = r.GpuName;

        ram.Visible = s.ShowRam && r.RamLoad.HasValue;
        ram.SetValue(r.RamUsed is float u ? u.ToString(u >= 100 ? "0" : "0.0", Fmt.Fr) : "—", " Go", Theme.Text);
        ram.SetLoad(r.RamLoad);
        ram.Root.ToolTip = r.RamUsed is float used && r.RamTotal is float total
            ? $"{used.ToString("0.0", Fmt.Fr)} Go utilisés sur {total.ToString("0.0", Fmt.Fr)} Go" : null;

        disk.Visible = s.ShowDisk && (r.DiskTemp.HasValue || r.DiskLoad.HasValue);
        SetTemp(disk, r.DiskTemp, f, 50, 60);
        disk.SetLoad(r.DiskLoad);
        disk.Root.ToolTip = r.DiskName is null ? "Activité du disque le plus sollicité" : $"Le plus chaud : {r.DiskName}\nBarre : activité du disque le plus sollicité";

        fan.Visible = s.ShowFan && r.FanRpm.HasValue;
        fan.SetValue(r.FanRpm is float rpm ? rpm.ToString("N0", Fmt.Fr) : "—", " tr/min", Theme.Text);

        string? notice = null;
        if (!app.IsAdmin && !app.Demo) notice = "Lance Temperator en administrateur pour lire les températures.";
        else if (s.ShowCpu && r.CpuTemp == null && ++updates > 3 && !Native.PawnIOInstalled.Value)
            notice = "Pilote PawnIO absent : température du CPU indisponible. Relance l'installeur de Temperator.";
        Notice.Text = notice ?? "";
        Notice.Visibility = notice == null ? Visibility.Collapsed : Visibility.Visible;

        Pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)));
    }

    static void SetTemp(MetricRow row, float? celsius, bool fahrenheit, float warn, float hot) =>
        row.SetValue(celsius is float c ? Fmt.Temp(c, fahrenheit).ToString("0", Fmt.Fr) : "—", fahrenheit ? "°F" : "°", Theme.ForTemp(celsius, warn, hot));

    static string? Lines(params string?[] lines)
    {
        var text = string.Join("\n", Array.FindAll(lines, l => !string.IsNullOrEmpty(l)));
        return text.Length > 0 ? text : null;
    }
}
