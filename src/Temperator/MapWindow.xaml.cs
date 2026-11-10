using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
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

    public MapWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        Machine.Text = Fmt.MachineLine();
        TitleBar.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (InvalidOperationException) { } };
        CloseX.MouseLeftButtonUp += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Refresh();
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
