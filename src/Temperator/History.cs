using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Temperator;

/// <summary>Les valeurs d'un capteur dans le temps (les plus anciennes sont oubliées au-delà de <c>keep</c>).</summary>
sealed class Series
{
    readonly Queue<(DateTime T, float V)> points = new();
    readonly TimeSpan keep;
    public Series(TimeSpan keep) => this.keep = keep;
    public IEnumerable<(DateTime T, float V)> Points => points;

    public void Add(DateTime t, float? value)
    {
        if (value is float v && !float.IsNaN(v) && !float.IsInfinity(v)) points.Enqueue((t, v));
        while (points.Count > 0 && t - points.Peek().T > keep) points.Dequeue();
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

/// <summary>Une série par mesure (températures, utilisation, consommation, réseau).</summary>
sealed class Metrics
{
    public readonly Series CpuTemp, GpuTemp, DiskTemp, CpuLoad, GpuLoad, RamLoad, Power, NetDown, NetUp;
    public Metrics(TimeSpan keep)
    {
        CpuTemp = new(keep); GpuTemp = new(keep); DiskTemp = new(keep);
        CpuLoad = new(keep); GpuLoad = new(keep); RamLoad = new(keep);
        Power = new(keep); NetDown = new(keep); NetUp = new(keep);
    }
    public Series[] All => new[] { CpuTemp, GpuTemp, DiskTemp, CpuLoad, GpuLoad, RamLoad, Power, NetDown, NetUp };
}

/// <summary>
/// Historique : les 10 dernières minutes en détail (chaque lecture), et les 24 dernières heures en moyennes de
/// 30 secondes, enregistrées dans %ProgramData%\Temperator\historique.csv pour survivre à un redémarrage.
/// </summary>
sealed class History
{
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LongKeep = TimeSpan.FromHours(24);
    static readonly TimeSpan Bucket = TimeSpan.FromSeconds(30);
    static readonly string[] Columns = { "cpu_temp", "gpu_temp", "disk_temp", "cpu_load", "gpu_load", "ram_load", "power_w", "net_down_bps", "net_up_bps" };

    /// <summary>Détail : 10 minutes.</summary>
    public readonly Metrics Fine = new(Keep);
    /// <summary>Moyennes de 30 s : 24 heures.</summary>
    public readonly Metrics Coarse = new(LongKeep);
    public event Action? Updated;

    // raccourcis (infobulles du widget)
    public Series CpuTemp => Fine.CpuTemp;
    public Series GpuTemp => Fine.GpuTemp;
    public Series DiskTemp => Fine.DiskTemp;
    public Series CpuLoad => Fine.CpuLoad;
    public Series GpuLoad => Fine.GpuLoad;
    public Series RamLoad => Fine.RamLoad;

    readonly double[] sum = new double[Columns.Length];
    readonly int[] count = new int[Columns.Length];
    DateTime bucketStart = DateTime.MinValue;
    string? file;

    static float?[] Values(Readings r) =>
        new[] { r.CpuTemp, r.GpuTemp, r.DiskTemp, r.CpuLoad, r.GpuLoad, r.RamLoad, r.TotalPower, r.NetDown, r.NetUp };

    public void Add(Readings r)
    {
        if (!r.Ready) return;
        var t = DateTime.UtcNow;
        var v = Values(r);
        var fine = Fine.All;
        for (int i = 0; i < v.Length; i++) fine[i].Add(t, v[i]);

        if (bucketStart == DateTime.MinValue) bucketStart = t;
        for (int i = 0; i < v.Length; i++)
            if (v[i] is float x && !float.IsNaN(x)) { sum[i] += x; count[i]++; }
        if (t - bucketStart >= Bucket) CloseBucket(t);
        Updated?.Invoke();
    }

    void CloseBucket(DateTime t)
    {
        var avg = new float?[Columns.Length];
        var coarse = Coarse.All;
        for (int i = 0; i < avg.Length; i++)
        {
            avg[i] = count[i] > 0 ? (float)(sum[i] / count[i]) : null;
            coarse[i].Add(t, avg[i]);
            sum[i] = 0;
            count[i] = 0;
        }
        bucketStart = t;
        if (file == null) return;
        try { File.AppendAllText(file, Line(t, avg, ",", CultureInfo.InvariantCulture) + "\n"); }
        catch (Exception ex) { App.Log(ex); file = null; }   // disque plein, droits… : on continue en mémoire
    }

    static string Line(DateTime utc, float?[] values, string sep, CultureInfo culture) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + sep +
        string.Join(sep, values.Select(v => v is float x ? x.ToString("0.##", culture) : ""));

    /// <summary>Recharge les 24 dernières heures enregistrées, et oublie le reste du fichier.</summary>
    public void Load(string folder)
    {
        file = Path.Combine(folder, "historique.csv");
        try
        {
            var keep = new List<string>();
            var limit = DateTime.UtcNow - LongKeep;
            if (File.Exists(file))
            {
                foreach (var line in File.ReadLines(file).Skip(1))
                {
                    var parts = line.Split(',');
                    if (parts.Length < 1 + Columns.Length ||
                        !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ||
                        t < limit) continue;
                    var coarse = Coarse.All;
                    for (int i = 0; i < Columns.Length; i++)
                        coarse[i].Add(t, float.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null);
                    keep.Add(line);
                }
            }
            File.WriteAllText(file, "time_utc," + string.Join(",", Columns) + "\n" + string.Concat(keep.Select(l => l + "\n")));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Exporte les 24 dernières heures (moyennes de 30 s) en CSV, lisible par Excel dans la langue choisie.</summary>
    public void ExportCsv(string path)
    {
        string sep = L.Fr ? ";" : ",";   // Excel en français attend des points-virgules et des virgules décimales
        var culture = L.Fr ? CultureInfo.GetCultureInfo("fr-FR") : CultureInfo.InvariantCulture;
        string[] head = L.Fr
            ? new[] { "date (UTC)", "temp CPU (°C)", "temp GPU (°C)", "temp disque (°C)", "util. CPU (%)", "util. GPU (%)", "util. RAM (%)", "conso (W)", "réseau ↓ (o/s)", "réseau ↑ (o/s)" }
            : new[] { "time (UTC)", "CPU temp (°C)", "GPU temp (°C)", "drive temp (°C)", "CPU load (%)", "GPU load (%)", "RAM load (%)", "power (W)", "network down (B/s)", "network up (B/s)" };

        // une ligne par instant : on regroupe les séries sur leurs horodatages
        var rows = new SortedDictionary<DateTime, float?[]>();
        var coarse = Coarse.All;
        for (int i = 0; i < coarse.Length; i++)
            foreach (var (t, v) in coarse[i].Points)
            {
                if (!rows.TryGetValue(t, out var row)) rows[t] = row = new float?[Columns.Length];
                row[i] = v;
            }
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(sep, head));
        foreach (var (t, row) in rows) sb.AppendLine(Line(t, row, sep, culture));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Captures du README : 10 minutes (et 24 heures) de valeurs d'exemple.</summary>
    public void FillDemo()
    {
        var now = DateTime.UtcNow;
        void Fill(Metrics m, int seconds, int step)
        {
            double k = seconds / 600.0;   // sur 24 h, des variations plus lentes
            for (int i = seconds; i >= 0; i -= step)
            {
                var t = now.AddSeconds(-i);
                double s = i / k;
                double x = (seconds - i) / (double)seconds, game = x > 0.45 && x < 0.8 ? 1 : 0;   // une partie de jeu au milieu
                m.CpuTemp.Add(t, (float)(52 + 20 * game + 4 * Math.Sin(s / 23.0) + 2 * Math.Sin(s / 7.0)));
                m.GpuTemp.Add(t, (float)(48 + 26 * game + 3 * Math.Sin(s / 31.0)));
                m.DiskTemp.Add(t, (float)(39 + 3 * x + Math.Sin(s / 50.0)));
                m.CpuLoad.Add(t, (float)Math.Clamp(18 + 45 * game + 12 * Math.Sin(s / 9.0), 0, 100));
                m.GpuLoad.Add(t, (float)Math.Clamp(6 + 85 * game + 5 * Math.Sin(s / 13.0), 0, 100));
                m.RamLoad.Add(t, (float)(62 + 14 * game + 2 * Math.Sin(s / 40.0)));
                m.Power.Add(t, (float)(35 + 110 * game + 8 * Math.Sin(s / 11.0)));
            }
        }
        Fill(Fine, 600, 2);
        Fill(Coarse, 86400, 30);
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
            ? L.T($"5 dernières minutes · min {format(r.Min)} · max {format(r.Max)}", $"Last 5 minutes · min {format(r.Min)} · max {format(r.Max)}")
            : L.T("L'historique se remplit…", "History is filling up…");
}
