using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Temperator;

/// <summary>Une ligne du widget : NOM · valeur · barre · pourcentage.</summary>
sealed class MetricRow
{
    public readonly Grid Root = new() { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
    readonly Palette palette;
    readonly TextBlock big;
    readonly Run value = new("…"), unit = new();
    readonly Border track, fill;
    readonly TextBlock pct;
    readonly ScaleTransform scale = new(0, 1);
    double shown = -1;
    bool hot;

    public MetricRow(string label, Palette palette)
    {
        this.palette = palette;
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

        var name = new TextBlock
        {
            Text = label, FontFamily = Theme.TitleFont, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = palette.Muted, VerticalAlignment = VerticalAlignment.Center,
        };
        value.FontSize = 20;
        value.FontWeight = FontWeights.SemiBold;
        value.Foreground = palette.Text;
        unit.FontSize = 12;
        unit.Foreground = palette.Muted;
        big = new TextBlock { FontFamily = Theme.TitleFont, VerticalAlignment = VerticalAlignment.Center };
        big.Inlines.Add(value);
        big.Inlines.Add(unit);

        fill = new Border { CornerRadius = new CornerRadius(2.5), Background = palette.Accent, RenderTransformOrigin = new Point(0, 0.5), RenderTransform = scale };
        track = new Border
        {
            Height = 5, CornerRadius = new CornerRadius(2.5), Background = palette.Track,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 0), Child = fill,
        };
        pct = new TextBlock { FontSize = 11.5, Foreground = palette.Soft, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

        Grid.SetColumn(big, 1);
        Grid.SetColumn(track, 2);
        Grid.SetColumn(pct, 3);
        Root.Children.Add(name);
        Root.Children.Add(big);
        Root.Children.Add(track);
        Root.Children.Add(pct);
    }

    /// <summary>Ligne sans barre : la valeur prend toute la largeur (ventilateur).</summary>
    public void Wide()
    {
        Grid.SetColumnSpan(big, 3);
        track.Visibility = pct.Visibility = Visibility.Collapsed;
    }

    public bool Visible
    {
        set => Root.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetValue(string text, string unitText, Brush brush)
    {
        if (value.Text != text) value.Text = text;
        if (unit.Text != unitText) unit.Text = unitText;
        value.Foreground = brush;
    }

    public void SetLoad(float? load)
    {
        if (load is not float l)
        {
            track.Visibility = pct.Visibility = Visibility.Hidden;
            return;
        }
        track.Visibility = pct.Visibility = Visibility.Visible;
        l = Math.Clamp(l, 0, 100);
        pct.Text = $"{l:0} %";
        fill.Background = l >= 90 ? palette.Warn : palette.Accent;
        if (Math.Abs(l - shown) < 0.5) return;
        shown = l;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(l / 100.0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Trop chaud : la valeur rougeoie en pulsant.</summary>
    public void SetHot(bool on)
    {
        if (on == hot) return;
        hot = on;
        if (!on)
        {
            big.Effect = null;
            return;
        }
        var glow = new DropShadowEffect { Color = palette.Hot.Color, ShadowDepth = 0, BlurRadius = 16, Opacity = 0.9 };
        var pulse = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(520)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(pulse, 30);
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
        big.Effect = glow;
    }
}
