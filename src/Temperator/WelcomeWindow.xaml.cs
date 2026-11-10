using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Temperator;

/// <summary>Premier lancement : choisir entre le mode simple (lite) et le mode complet.</summary>
public partial class WelcomeWindow : Window
{
    static readonly Brush Selected = Frozen(new SolidColorBrush(Color.FromRgb(0x4E, 0xD8, 0xC0)));
    static readonly Brush SelectedBg = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 0x4E, 0xD8, 0xC0)));
    static readonly Brush Idle = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xF1, 0xF3, 0xF6)));
    static readonly Brush IdleBg = Frozen(new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)));

    /// <summary>Le mode choisi : <see cref="AppSettings.Lite"/> ou <see cref="AppSettings.Full"/>.</summary>
    public string Choice { get; private set; }

    public WelcomeWindow(string current)
    {
        InitializeComponent();
        Choice = current == AppSettings.Full ? AppSettings.Full : AppSettings.Lite;

        // aperçu du mode simple : de vraies jauges
        var palette = new Palette();
        var gauges = new StackPanel { Orientation = Orientation.Horizontal, LayoutTransform = new ScaleTransform(0.82, 0.82) };
        foreach (var (name, temp, load, hot) in new[] { ("CPU", 58f, 23f, 85f), ("GPU", 64f, 41f, 87f), ("RAM", -1f, 72f, 0f) })
        {
            var tile = new GaugeTile(name, palette);
            if (temp > 0) tile.Set(temp.ToString("0"), "°", palette.ForTemp(temp, hot - 15, hot), (temp - 20) / 80, palette.ForTemp(temp, hot - 15, hot), $"{load:0} %");
            else tile.Set("11,4", " Go", palette.Text, load / 100, palette.Accent, $"{load:0} %");
            gauges.Children.Add(tile.Root);
        }
        LitePreview.Content = gauges;

        // aperçu du mode complet : ce qu'on peut régler
        foreach (var chip in new[] { "Translucide", "Sans fond", "Flammes", "Seuils", "Alertes", "Historique", "Clics traversants", "Raccourci", "Au-dessus de tout" })
            FullChips.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(0, 0, 5, 5),
                Background = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 0xF1, 0xF3, 0xF6))),
                Child = new TextBlock { Text = chip, FontSize = 11, Foreground = Theme.Soft },
            });

        CardLite.MouseLeftButtonUp += (_, e) => Pick(AppSettings.Lite, e);
        CardFull.MouseLeftButtonUp += (_, e) => Pick(AppSettings.Full, e);
        CardLite.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Done(); };
        CardFull.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Done(); };
        BtnContinue.Click += (_, _) => Done();
        CloseX.MouseLeftButtonUp += (_, _) => Done();   // fermer = garder le choix sélectionné
        Frame.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == Frame) try { DragMove(); } catch (InvalidOperationException) { } };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right) { Pick(Choice == AppSettings.Lite ? AppSettings.Full : AppSettings.Lite, null); e.Handled = true; }
            else if (e.Key == Key.Escape) Done();
        };
        ContentRendered += (_, _) => Activate();
        Highlight();
    }

    void Pick(string mode, MouseButtonEventArgs? e)
    {
        Choice = mode;
        Highlight();
        if (e != null) e.Handled = true;
    }

    void Highlight()
    {
        bool lite = Choice == AppSettings.Lite;
        CardLite.BorderBrush = lite ? Selected : Idle;
        CardLite.Background = lite ? SelectedBg : IdleBg;
        CardFull.BorderBrush = lite ? Idle : Selected;
        CardFull.Background = lite ? IdleBg : SelectedBg;
    }

    void Done() => Close();   // le choix est lu par App à la fermeture

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
