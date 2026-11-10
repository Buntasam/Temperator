using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Temperator;
using Xunit;

// la langue de l'interface est globale (L) : les tests ne tournent pas en parallèle
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Temperator.Tests;

public class UpdaterTests
{
    [Theory]
    [InlineData("v1.6.0", "1.5.0", true)]
    [InlineData("1.6.0", "1.5.9", true)]
    [InlineData("v1.10.0", "1.9.0", true)]   // comparaison de versions, pas de texte
    [InlineData("v1.5.0", "1.5.0", false)]
    [InlineData("v1.4.0", "1.5.0", false)]
    [InlineData("nightly", "1.0.0", false)]
    public void NewerVersion(string tag, string installed, bool newer) =>
        Assert.Equal(newer, Updater.NewerVersion(tag, installed) != null);

    [Fact]
    public void ExpectedHash_reads_sha256sum_format()
    {
        string sums = "aaaa  Temperator-Setup-1.5.0.exe\nBBBB *Temperator-Setup-1.6.0.exe\r\n";
        Assert.Equal("aaaa", Updater.ExpectedHash(sums, "Temperator-Setup-1.5.0.exe"));
        Assert.Equal("BBBB", Updater.ExpectedHash(sums, "temperator-setup-1.6.0.exe"));
        Assert.Null(Updater.ExpectedHash(sums, "Temperator-Setup-9.9.9.exe"));
    }

    [Fact]
    public void Matches_only_the_announced_fingerprint()
    {
        byte[] data = Encoding.UTF8.GetBytes("installeur");
        string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        Assert.True(Updater.Matches(data, hash));
        Assert.False(Updater.Matches(data, hash.Replace(hash[0], hash[0] == 'a' ? 'b' : 'a')));
        Assert.False(Updater.Matches(data, null));
    }
}

public class FormatTests
{
    [Fact]
    public void Rates_in_english()
    {
        L.Init(L.English);
        Assert.Equal(("12.3", "MB/s"), Fmt.RateParts(12.3e6f));
        Assert.Equal(("999", "B/s"), Fmt.RateParts(999));
        Assert.Equal(("150", "MB/s"), Fmt.RateParts(150e6f));   // au-delà de 100 : sans décimale
    }

    [Fact]
    public void Rates_and_units_in_french()
    {
        L.Init(L.French);
        Assert.Equal(("1,5", "Go/s"), Fmt.RateParts(1.5e9f));
        Assert.Equal("Go", Fmt.GB);
        Assert.Equal("tr/min", Fmt.Rpm);
        Assert.Equal("45 °C", Fmt.Value("Temperature", 45.2f, false));
        Assert.Equal("113 °F", Fmt.Value("Temperature", 45f, true));
        Assert.Equal("—", Fmt.Value("Temperature", null, false));
    }

    [Fact]
    public void Fahrenheit_conversion() => Assert.Equal(212f, Fmt.Temp(100, true));
}

public class HotkeyTests
{
    [Fact]
    public void Parses_letters_with_modifiers()
    {
        Assert.True(Native.ParseHotkey("Ctrl+Alt+T", out uint mods, out uint key));
        Assert.Equal(Native.MOD_CONTROL | Native.MOD_ALT, mods);
        Assert.Equal((uint)'T', key);

        Assert.True(Native.ParseHotkey("ctrl + maj + h", out mods, out key));
        Assert.Equal(Native.MOD_CONTROL | Native.MOD_SHIFT, mods);
        Assert.Equal((uint)'H', key);
    }

    [Theory]
    [InlineData("T")]            // pas de modificateur
    [InlineData("Ctrl+Alt")]     // pas de touche
    [InlineData("Ctrl+F12")]     // touche non gérée
    [InlineData("")]
    public void Rejects_invalid(string text) => Assert.False(Native.ParseHotkey(text, out _, out _));
}

public class SettingsTests
{
    [Fact]
    public void Migrates_settings_from_version_1_5()
    {
        var s = AppSettings.Parse("""{ "Gauges": false, "GameMode": "compact", "Scale": 9, "Accent": "rouge" }""");
        Assert.Equal(AppSettings.Rows, s.Layout);
        Assert.Equal(AppSettings.GameProfile, s.GameMode);
        Assert.Equal(2.5, s.Scale);
        Assert.Equal("#4ED8C0", s.Accent);
        Assert.DoesNotContain("\"Gauges\"", s.ToJson());   // l'ancien réglage n'est plus écrit
    }

    [Fact]
    public void Order_keeps_known_items_once_and_adds_new_ones()
    {
        var s = AppSettings.Parse("""{ "Order": ["gpu", "inconnu", "gpu", "cpu"] }""");
        Assert.Equal("gpu", s.Order[0]);
        Assert.Equal("cpu", s.Order[1]);
        Assert.Equal(MetricKeys.All.Length, s.Order.Count);
        Assert.Equal(s.Order.Count, s.Order.Distinct().Count());
    }

    [Fact]
    public void Defaults_round_trip()
    {
        var s = new AppSettings();
        var back = AppSettings.Parse(s.ToJson());
        Assert.Equal(s.Layout, back.Layout);
        Assert.Equal(s.Order, back.Order);
        Assert.Equal(s.GameShow, back.GameShow);
        Assert.True(back.Shows(MetricKeys.Cpu));
        back.SetShows(MetricKeys.Cpu, false);
        Assert.False(back.Shows(MetricKeys.Cpu));
    }
}

public class HeatTests
{
    [Fact]
    public void Too_hot_with_a_3_degree_margin()
    {
        Assert.True(MainWindow.Hot(false, 85, 85));    // atteint le seuil
        Assert.False(MainWindow.Hot(false, 84, 85));
        Assert.True(MainWindow.Hot(true, 83, 85));     // reste rouge jusqu'à 3 °C en dessous
        Assert.False(MainWindow.Hot(true, 81.9f, 85));
        Assert.False(MainWindow.Hot(true, null, 85));
        Assert.False(MainWindow.Hot(false, 99, 0));    // pas de seuil : jamais « trop chaud »
    }

    [Fact]
    public void Fps_rate() => Assert.Equal(60f, FpsCounter.Rate(120, 2.0));
}

public class HistoryTests
{
    [Fact]
    public void Keeps_the_last_24_hours_on_disk()
    {
        L.Init(L.English);
        string dir = Path.Combine(Path.GetTempPath(), "temperator-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string recent = DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ");
            string old = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-ddTHH:mm:ssZ");
            File.WriteAllText(Path.Combine(dir, "historique.csv"),
                "time_utc,cpu_temp,gpu_temp,disk_temp,cpu_load,gpu_load,ram_load,power_w,net_down_bps,net_up_bps\n" +
                $"{old},40,40,30,10,10,50,30,0,0\n" +
                $"{recent},61.5,55,38,22,9,70,48,1000,200\n");

            var history = new History();
            history.Load(dir);
            Assert.Single(history.Coarse.CpuTemp.Points);
            Assert.Equal(61.5f, history.Coarse.CpuTemp.Points.First().V);
            Assert.Equal(2, File.ReadAllLines(Path.Combine(dir, "historique.csv")).Length);   // en-tête + la ligne récente

            string export = Path.Combine(dir, "export.csv");
            history.ExportCsv(export);
            var lines = File.ReadAllLines(export);
            Assert.StartsWith("time (UTC),CPU temp", lines[0]);
            Assert.Contains("61.5", lines[1]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Series_forgets_old_values_and_gives_its_range()
    {
        var series = new Series(TimeSpan.FromMinutes(10));
        var now = DateTime.UtcNow;
        series.Add(now.AddMinutes(-20), 99);
        series.Add(now.AddMinutes(-1), 40);
        series.Add(now, 60);
        series.Add(now, float.NaN);   // ignoré
        Assert.Equal(2, series.Points.Count());
        Assert.Equal((40f, 60f), series.Range(TimeSpan.FromMinutes(5)));
    }
}

public class MetricViewTests
{
    [Fact]
    public void Temperature_colors_follow_the_thresholds()
    {
        L.Init(L.English);
        var palette = new Palette();
        var settings = new AppSettings();   // CPU trop chaud à 85 °C
        var views = new MetricViews(palette, () => settings);

        var hot = views.Build(MetricKeys.Cpu, new Readings { Ready = true, CpuName = "CPU", CpuTemp = 90, CpuLoad = 50 }, null);
        Assert.Same(palette.Hot, hot.ValueBrush);
        Assert.Equal(85f, hot.HotLimit);
        Assert.Equal("90", hot.Value);
        Assert.Equal("50 %", hot.Side);

        var ok = views.Build(MetricKeys.Cpu, new Readings { Ready = true, CpuName = "CPU", CpuTemp = 50 }, null);
        Assert.Same(palette.Accent, ok.ValueBrush);
    }

    [Fact]
    public void Items_without_data_are_unavailable()
    {
        var views = new MetricViews(new Palette(), () => new AppSettings());
        var empty = new Readings { Ready = true };
        Assert.False(views.Build(MetricKeys.Gpu, empty, null).Available);
        Assert.False(views.Build(MetricKeys.Battery, empty, null).Available);
        Assert.False(views.Build(MetricKeys.Fps, empty, null).Available);
        Assert.True(views.Build(MetricKeys.Fps, empty, 144).Available);
    }

    [Fact]
    public void Network_shows_both_directions()
    {
        L.Init(L.English);
        var v = new MetricViews(new Palette(), () => new AppSettings())
            .Build(MetricKeys.Net, new Readings { Ready = true, NetDown = 12.3e6f, NetUp = 1.2e6f }, null);
        Assert.Equal("↓ 12.3  ↑ 1.2", v.Value);
        Assert.Equal(" MB/s", v.Unit);
        Assert.Equal("12.3", v.TileValue);
    }
}
