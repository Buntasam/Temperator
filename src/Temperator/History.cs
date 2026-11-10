using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Temperator;

/// <summary>Les valeurs d'un capteur sur les 10 dernières minutes.</summary>
sealed class Series
{
    readonly Queue<(DateTime T, float V)> points = new();
    public IEnumerable<(DateTime T, float V)> Points => points;

    public void Add(DateTime t, float? value)
    {
        if (value is float v && !float.IsNaN(v) && !float.IsInfinity(v)) points.Enqueue((t, v));
        while (points.Count > 0 && t - points.Peek().T > History.Keep) points.Dequeue();
    }

    public (float Min, float Max)? Range(TimeSpan span)
    {
        var from = DateTime.UtcNow - span;
        float min = float.MaxValue, max = float.MinValue;
        foreach (var (t, v) in points)
        {
            if (t < from) continue;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        return min <= max ? (min, max) : null;
    }
}

/// <summary>Historique en mémoire (10 minutes), alimenté à chaque lecture des capteurs.</summary>
sealed class History
{
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);
    public readonly Series CpuTemp = new(), GpuTemp = new(), DiskTemp = new(), CpuLoad = new(), GpuLoad = new(), RamLoad = new();
    public event Action? Updated;

    public void Add(Readings r)
    {
        if (!r.Ready) return;
        var t = DateTime.UtcNow;
        CpuTemp.Add(t, r.CpuTemp); GpuTemp.Add(t, r.GpuTemp); DiskTemp.Add(t, r.DiskTemp);
        CpuLoad.Add(t, r.CpuLoad); GpuLoad.Add(t, r.GpuLoad); RamLoad.Add(t, r.RamLoad);
        Updated?.Invoke();
    }

    /// <summary>Captures du README : 10 minutes de valeurs d'exemple.</summary>
    public void FillDemo()
    {
        var now = DateTime.UtcNow;
        for (int s = 600; s >= 0; s -= 2)
        {
            var t = now.AddSeconds(-s);
            double x = (600 - s) / 600.0, game = x > 0.45 && x < 0.8 ? 1 : 0;   // une partie de jeu au milieu
            CpuTemp.Add(t, (float)(52 + 20 * game + 4 * Math.Sin(s / 23.0) + 2 * Math.Sin(s / 7.0)));
            GpuTemp.Add(t, (float)(48 + 26 * game + 3 * Math.Sin(s / 31.0)));
            DiskTemp.Add(t, (float)(39 + 3 * x + Math.Sin(s / 50.0)));
            CpuLoad.Add(t, (float)Math.Clamp(18 + 45 * game + 12 * Math.Sin(s / 9.0), 0, 100));
            GpuLoad.Add(t, (float)Math.Clamp(6 + 85 * game + 5 * Math.Sin(s / 13.0), 0, 100));
            RamLoad.Add(t, (float)(62 + 14 * game + 2 * Math.Sin(s / 40.0)));
        }
        Updated?.Invoke();
    }
}

sealed record ChartLine(Series Series, Brush Brush, string Name);

/// <summary>Courbes en direct (temps à l'horizontale, « maintenant » à droite), avec un seuil en pointillés.</summary>
sealed class HistoryChart : FrameworkElement
{
    static readonly Brush GridBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0xF1, 0xF3, 0xF6)));
    static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0x97, 0xA4)));
    static readonly Pen ThresholdPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0x5A, 0x5F))), 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) });
    static readonly Typeface Face = new("Segoe UI");

    readonly History history;
    readonly ChartLine[] lines;
    readonly TimeSpan span;
    readonly double min, max;
    readonly Func<double, string> label;
    readonly Func<double>? threshold;

    /// <param name="min">bas de l'échelle ; le haut (max) s'agrandit si une valeur le dépasse</param>
    public HistoryChart(History history, TimeSpan span, double min, double max, Func<double, string> label, Func<double>? threshold, params ChartLine[] lines)
    {
        this.history = history;
        this.span = span;
        this.min = min;
        this.max = max;
        this.label = label;
        this.threshold = threshold;
        this.lines = lines;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => history.Updated += InvalidateVisual;
        Unloaded += (_, _) => history.Updated -= InvalidateVisual;
    }

    protected override void OnRender(DrawingContext dc)
    {
        const double left = 32, top = 6, bottom = 4;
        double w = ActualWidth, h = ActualHeight;
        if (w < left + 20 || h < 20) return;
        var plot = new Rect(left, top, w - left - 2, h - top - bottom);
        var now = DateTime.UtcNow;

        double lo = min, hi = max;
        foreach (var line in lines)
            if (line.Series.Range(span) is { } r) hi = Math.Max(hi, Math.Ceiling((r.Max + 3) / 10) * 10);
        double? limit = threshold?.Invoke();
        if (limit is double l && l > hi - 2) hi = l + 10;

        double Y(double v) => plot.Bottom - (v - lo) / (hi - lo) * plot.Height;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 3 lignes de repère, avec leur valeur à gauche
        var gridPen = new Pen(GridBrush, 1);
        foreach (double v in new[] { lo, (lo + hi) / 2, hi })
        {
            double y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var text = new FormattedText(label(v), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 9.5, LabelBrush, dpi);
            dc.DrawText(text, new Point(left - 5 - text.Width, y - text.Height / 2));
        }
        if (limit is double lim && lim >= lo && lim <= hi)
            dc.DrawLine(ThresholdPen, new Point(plot.Left, Y(lim)), new Point(plot.Right, Y(lim)));

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, 0, plot.Width, h)));
        foreach (var line in lines)
        {
            var g = new StreamGeometry();
            bool started = false;
            Point first = default, last = default;
            using (var ctx = g.Open())
            {
                foreach (var (t, v) in line.Series.Points)
                {
                    var age = now - t;
                    if (age > span) continue;
                    var p = new Point(plot.Right - age.TotalSeconds / span.TotalSeconds * plot.Width, Y(Math.Clamp(v, lo, hi)));
                    if (!started) { ctx.BeginFigure(p, false, false); first = p; started = true; }
                    else ctx.LineTo(p, true, true);
                    last = p;
                }
            }
            if (!started) continue;
            g.Freeze();

            if (lines.Length == 1 && line.Brush is SolidColorBrush sb)   // une seule courbe : zone remplie dessous, discrète
            {
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(first.X, plot.Bottom), true, true);
                    foreach (var (t, v) in line.Series.Points)
                    {
                        var age = now - t;
                        if (age <= span) ctx.LineTo(new Point(plot.Right - age.TotalSeconds / span.TotalSeconds * plot.Width, Y(Math.Clamp(v, lo, hi))), false, false);
                    }
                    ctx.LineTo(new Point(last.X, plot.Bottom), false, false);
                }
                var c = sb.Color;
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B)), null, area);
            }
            dc.DrawGeometry(null, new Pen(line.Brush, 1.6) { LineJoin = PenLineJoin.Round }, g);
            dc.DrawEllipse(line.Brush, null, last, 2.4, 2.4);   // la valeur actuelle
        }
        dc.Pop();
    }

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

/// <summary>Infobulle d'un composant : son nom (et détails), et sa courbe des 5 dernières minutes.</summary>
sealed class HistoryTip : StackPanel
{
    static readonly TimeSpan Span = TimeSpan.FromMinutes(5);
    readonly TextBlock text = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBlock stats = new() { FontSize = 11, Foreground = Theme.Muted, Margin = new Thickness(0, 4, 0, 0) };
    readonly Series series;
    readonly Func<float, string> format;

    public HistoryTip(History history, Series series, bool temperature, Func<bool> fahrenheit, Func<double>? threshold)
    {
        this.series = series;
        Width = 250;
        format = temperature
            ? v => Fmt.Temp(v, fahrenheit()).ToString("0", Fmt.Fr) + (fahrenheit() ? " °F" : " °C")
            : v => $"{v:0} %";
        Children.Add(text);
        Children.Add(new HistoryChart(history, Span, temperature ? 20 : 0, temperature ? 90 : 100,
            v => temperature ? Fmt.Temp((float)v, fahrenheit()).ToString("0", Fmt.Fr) + "°" : $"{v:0}%",
            threshold, new ChartLine(series, Theme.Accent, "")) { Height = 70 });
        Children.Add(stats);
        Loaded += (_, _) => { history.Updated += UpdateStats; UpdateStats(); };
        Unloaded += (_, _) => history.Updated -= UpdateStats;
    }

    public string? Text
    {
        set
        {
            text.Text = value ?? "";
            text.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    void UpdateStats() =>
        stats.Text = series.Range(Span) is { } r
            ? $"5 dernières minutes · min {format(r.Min)} · max {format(r.Max)}"
            : "L'historique se remplit…";
}
