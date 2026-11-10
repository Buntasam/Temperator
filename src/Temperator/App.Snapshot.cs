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

/// <summary>Captures d'écran pour le README et le développement (--snapshot dossier [--demo] [--en]).</summary>
public partial class App
{
    // ------------------------------------------------------------------ captures d'écran (développement / README)

    async Task Snapshot(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Settings = new AppSettings { ShowFan = true, Layout = AppSettings.Rows, Mode = AppSettings.Full };
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
                Settings.Layout = AppSettings.Gauges;
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
                Settings.Layout = AppSettings.Rows;
                widget.ApplySettings();
                widget.Apply(normal);
                await Task.Delay(900);
                SavePng(widget, Path.Combine(dir, "widget-lignes-tout.png"));

                // barre fine, puis thème clair (fond opaque)
                Settings.Layout = AppSettings.Bar;
                widget.ApplySettings();
                widget.Apply(normal);
                await Task.Delay(700);
                SavePng(widget, Path.Combine(dir, "widget-barre.png"));
                Settings.Layout = AppSettings.Gauges;
                Settings.Theme = AppSettings.Light;
                Settings.ShowNet = Settings.ShowPower = false;
                widget.ApplySettings();
                widget.Apply(normal);
                await Task.Delay(700);
                SavePng(widget, Path.Combine(dir, "widget-clair.png"));
                Settings.Theme = AppSettings.Dark;

                // profil « Jeu » : plein écran simulé, avec des FPS d'exemple
                widget.DemoFps = 144;
                widget.SimulateGame(true);
                await Task.Delay(900);
                SavePng(widget, Path.Combine(dir, "widget-jeu.png"), new LinearGradientBrush(Color.FromRgb(0x2B, 0x1B, 0x3A), Color.FromRgb(0x0E, 0x2A, 0x35), 35));
                widget.SimulateGame(false);
                widget.DemoFps = null;
                Settings.Accent = "#4ED8C0";
                Settings.Layout = AppSettings.Gauges;
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

    /// <summary>
    /// Vérifie la mesure des FPS (en administrateur) : une fenêtre animée de Temperator lui-même est comptée
    /// pendant 3 secondes ; le résultat est écrit dans le fichier.
    /// </summary>
    async Task FpsTest(string file)
    {
        var spin = new RotateTransform();
        var square = new Border { Width = 120, Height = 120, Background = Theme.Accent, RenderTransform = spin, RenderTransformOrigin = new Point(0.5, 0.5) };
        var w = new Window { Title = "Temperator – test FPS", Width = 320, Height = 260, Content = square, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
        w.Show();
        spin.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });

        using var counter = new FpsCounter();
        counter.Start();
        await Task.Delay(1000);
        counter.Sample(Environment.ProcessId);   // remise à zéro
        await Task.Delay(3000);
        float? fps = counter.Sample(Environment.ProcessId);
        File.WriteAllText(file, $"admin={IsAdmin}\nsession={counter.Running}\nfps={fps?.ToString("0.0") ?? "aucune image comptée"}\nerreur={counter.LastError ?? "aucune"}\n");
        w.Close();
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
