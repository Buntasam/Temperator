using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace Temperator;

/// <summary>« Plan du PC » : tout le matériel détecté et tous ses capteurs, en direct.</summary>
public partial class MapWindow : Window
{
    readonly App app;
    List<HardwareVM> items = new();
    int signature = -1;

    TimeSpan span = History.Keep;

    public MapWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        Title = L.T("Plan du PC – Temperator", "PC map – Temperator");
        Heading.Text = L.T("Plan du PC", "PC map");
        Empty.Text = L.T("Lecture des capteurs…", "Reading sensors…");
        Machine.Text = Fmt.MachineLine();
        HistoryHost.Content = HistoryCard();
        TitleBar.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (InvalidOperationException) { } };
        CloseX.MouseLeftButtonUp += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Refresh();
    }

    /// <summary>Change la durée affichée par l'historique (10 min, 1 h ou 24 h).</summary>
    public void ShowSpan(TimeSpan value)
    {
        span = value;
        HistoryHost.Content = HistoryCard();
    }

    /// <summary>Carte « Historique » : températures et utilisation, sur 10 minutes (détail) ou 1 h / 24 h (moyennes de 30 s).</summary>
    UIElement HistoryCard()
    {
        var h = app.History;
        var m = span <= History.Keep ? h.Fine : h.Coarse;
        bool F() => app.Settings.Fahrenheit;
        string Deg(double v) => Fmt.Temp((float)v, F()).ToString("0", Fmt.Fr) + "°";
        Brush cpuB = Theme.Accent, gpuB = Brush("#7AA2FF"), thirdB = Brush("#FFB547");
        string diskName = L.T("Disque", "Drive");

        var panel = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        // durée : 10 min · 1 h · 24 h
        var spans = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 10, 0, 2) };
        foreach (var (label, value) in new[] { ("10 min", History.Keep), (L.T("1 h", "1 h"), TimeSpan.FromHours(1)), (L.T("24 h", "24 h"), History.LongKeep) })
        {
            var pill = new RadioButton { Style = (Style)FindResource("Pill"), GroupName = "span", Content = label, IsChecked = value == span, Margin = new Thickness(6, 0, 0, 0) };
            pill.Click += (_, _) => ShowSpan(value);
            spans.Children.Add(pill);
        }
        var export = new Button { Style = (Style)FindResource("ActionButton"), Content = L.T("Exporter CSV…", "Export CSV…"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        export.Click += (_, _) => app.ExportHistory(this);
        spans.Children.Add(export);

        head.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center,
            Background = Brush("#224ED8C0"), BorderBrush = Brush("#554ED8C0"), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = L.T("HISTORIQUE", "HISTORY"), FontFamily = Theme.TitleFont, FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Theme.Accent },
        });
        string title = span <= History.Keep ? L.T("10 dernières minutes", "Last 10 minutes")
                     : span <= TimeSpan.FromHours(1) ? L.T("Dernière heure", "Last hour") : L.T("Dernières 24 heures", "Last 24 hours");
        head.Children.Add(new TextBlock { Text = title, Margin = new Thickness(10, 0, 0, 0), FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(head);
        panel.Children.Add(spans);

        panel.Children.Add(Section(L.T("TEMPÉRATURES", "TEMPERATURES"), (cpuB, "CPU"), (gpuB, "GPU"), (thirdB, diskName)));
        panel.Children.Add(new HistoryChart(h, span, 20, 90, Deg, null,
            new ChartLine(m.CpuTemp, cpuB, "CPU"), new ChartLine(m.GpuTemp, gpuB, "GPU"), new ChartLine(m.DiskTemp, thirdB, diskName)) { Height = 120 });
        panel.Children.Add(Section(L.T("UTILISATION", "LOAD"), (cpuB, "CPU"), (gpuB, "GPU"), (thirdB, "RAM")));
        panel.Children.Add(new HistoryChart(h, span, 0, 100, v => $"{v:0}%", null,
            new ChartLine(m.CpuLoad, cpuB, "CPU"), new ChartLine(m.GpuLoad, gpuB, "GPU"), new ChartLine(m.RamLoad, thirdB, "RAM")) { Height = 90 });
        if (span > History.Keep)
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Moyennes de 30 secondes, conservées même après un redémarrage.", "30-second averages, kept across restarts."),
                FontSize = 11, Foreground = Theme.Muted, Margin = new Thickness(0, 6, 0, 0),
            });

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(16, 13, 16, 12), CornerRadius = new CornerRadius(12),
            Background = Brush("#0CFFFFFF"), BorderBrush = Brush("#14FFFFFF"), BorderThickness = new Thickness(1), Child = panel,
        };
    }

    /// <summary>Titre de section et légende des couleurs.</summary>
    static UIElement Section(string title, params (Brush Brush, string Name)[] legend)
    {
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 2) };
        var keys = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (brush, name) in legend)
        {
            keys.Children.Add(new Border { Width = 10, Height = 3, CornerRadius = new CornerRadius(1.5), Background = brush, Margin = new Thickness(12, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            keys.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = Theme.Soft });
        }
        DockPanel.SetDock(keys, Dock.Right);
        row.Children.Add(keys);
        row.Children.Add(new TextBlock { Text = title, FontFamily = Theme.TitleFont, FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Theme.Muted });
        return row;
    }

    static Brush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public void Refresh()
    {
        bool f = app.Settings.Fahrenheit;
        lock (app.Hub.Sync)
        {
            var hw = app.Hub.Hardware.SelectMany(Flatten).ToList();
            int sig = hw.Count * 10007 + hw.Sum(h => h.Sensors.Length);
            if (sig != signature)   // matériel ou capteurs ajoutés : on reconstruit la liste
            {
                signature = sig;
                double offset = Scroller.VerticalOffset;
                items = hw.Select(h => new HardwareVM(h)).Where(v => v.Groups.Count > 0).ToList();
                List.ItemsSource = items;
                Scroller.ScrollToVerticalOffset(offset);
            }
            foreach (var vm in items) vm.Refresh(f);
        }
        Empty.Visibility = items.Exists(i => i.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
    }

    static IEnumerable<IHardware> Flatten(IHardware h)
    {
        yield return h;
        foreach (var sub in h.SubHardware)
            foreach (var x in Flatten(sub)) yield return x;
    }
}

public sealed class HardwareVM : INotifyPropertyChanged
{
    public string Kind { get; }
    public string Name { get; }
    public List<GroupVM> Groups { get; }

    public HardwareVM(IHardware h)
    {
        Kind = Fmt.Kind(h);
        Name = h.Name;
        Groups = h.Sensors
            .GroupBy(s => s.SensorType.ToString())
            .OrderBy(g => Array.IndexOf(Fmt.GroupOrder, g.Key) is var i && i >= 0 ? i : 99)
            .Select(g => new GroupVM(Fmt.Group(g.Key), g.OrderBy(s => s.Index).Select(s => new SensorVM(s)).ToList()))
            .ToList();
    }

    public Visibility Visibility { get; private set; } = Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh(bool fahrenheit)
    {
        foreach (var g in Groups) g.Refresh(fahrenheit);
        var v = Groups.Exists(g => g.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        if (v != Visibility) { Visibility = v; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visibility))); }
    }
}

public sealed class GroupVM : INotifyPropertyChanged
{
    public string Title { get; }
    public List<SensorVM> Sensors { get; }
    public Visibility Visibility { get; private set; } = Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public GroupVM(string title, List<SensorVM> sensors) { Title = title; Sensors = sensors; }

    public void Refresh(bool fahrenheit)
    {
        foreach (var s in Sensors) s.Refresh(fahrenheit);
        var v = Sensors.Exists(s => s.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        if (v != Visibility) { Visibility = v; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visibility))); }
    }
}

public sealed class SensorVM : INotifyPropertyChanged
{
    readonly ISensor sensor;
    readonly string type;
    public string Name { get; }
    public string Value { get; private set; } = "—";
    public Brush Brush { get; private set; } = Theme.Text;
    /// <summary>Caché tant que le capteur n'a jamais donné de valeur (droits, pilote…).</summary>
    public Visibility Visibility { get; private set; } = Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public SensorVM(ISensor s)
    {
        sensor = s;
        type = s.SensorType.ToString();
        Name = s.Name;
    }

    public void Refresh(bool fahrenheit)
    {
        float? v = sensor.Value;
        var text = Fmt.Value(type, v, fahrenheit);
        var brush = type == "Temperature" && !SensorHub.IsDelta(sensor) ? Theme.ForTemp(v, 70, 85) : Theme.Text;
        if (text != Value) { Value = text; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
        if (!ReferenceEquals(brush, Brush)) { Brush = brush; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Brush))); }
        if (v.HasValue && !float.IsNaN(v.Value) && Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visibility)));
        }
    }
}
