using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Temperator;

/// <summary>Anneau ouvert en bas (270°, comme le logo) qui se remplit de 0 à 1.</summary>
sealed class ArcGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    Brush fill = Brushes.Teal;
    public Brush Track { get; init; } = Brushes.Gray;
    public double Thickness { get; init; } = 6;

    public Brush Fill
    {
        get => fill;
        set
        {
            if (ReferenceEquals(fill, value)) return;
            fill = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double r = Math.Min(ActualWidth, ActualHeight) / 2 - Thickness / 2;
        if (r <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawGeometry(null, Pen(Track), Arc(c, r, 1));
        double v = Math.Clamp(Value, 0, 1);
        if (v > 0.004) dc.DrawGeometry(null, Pen(fill), Arc(c, r, v));
    }

    Pen Pen(Brush b) => new(b, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    static Geometry Arc(Point c, double r, double fraction)
    {
        double sweep = 270 * fraction;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(c, r, 135), false, false);   // en bas à gauche, puis sens horaire
            ctx.ArcTo(At(c, r, 135 + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    static Point At(Point c, double r, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
    }
}

/// <summary>Affichage « jauges rondes » : l'anneau suit la température, la valeur au centre, le nom dans l'ouverture.</summary>
sealed class GaugeTile
{
    const double Size = 68;
    public readonly Grid Root = new() { Margin = new Thickness(3, 0, 3, 0), Background = Brushes.Transparent };
    readonly Palette palette;
    readonly ArcGauge ring;
    readonly Run value = new("…"), unit = new();
    readonly TextBlock sub;
    double shown = -1;
    bool hot;

    public GaugeTile(string label, Palette palette)
    {
        this.palette = palette;
        ring = new ArcGauge { Width = Size, Height = Size, Track = palette.Track, Fill = palette.Accent, Thickness = 6 };

        value.FontSize = 19;
        value.FontWeight = FontWeights.SemiBold;
        value.Foreground = palette.Text;
        unit.FontSize = 11;
        unit.Foreground = palette.Muted;
        var big = new TextBlock { FontFamily = Theme.TitleFont, HorizontalAlignment = HorizontalAlignment.Center };
        big.Inlines.Add(value);
        big.Inlines.Add(unit);
        sub = new TextBlock { FontSize = 10, Foreground = palette.Soft, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        center.Children.Add(big);
        center.Children.Add(sub);

        var name = new TextBlock
        {
            Text = label, FontFamily = Theme.TitleFont, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = palette.Muted,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -1),
        };

        var face = new Grid { Width = Size, Height = Size };
        face.Children.Add(ring);
        face.Children.Add(center);
        face.Children.Add(name);

        // le feu de cette jauge seulement : derrière l'anneau, fondu en rond sur les bords (pas de « boîte »)
        var fade = new RadialGradientBrush { Center = new Point(0.5, 0.62), GradientOrigin = new Point(0.5, 0.75), RadiusX = 0.5, RadiusY = 0.7 };
        fade.GradientStops.Add(new GradientStop(Colors.White, 0));
        fade.GradientStops.Add(new GradientStop(Colors.White, 0.4));
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        fade.Freeze();
        var flames = new Canvas { Margin = new Thickness(-3, -6, -3, -10), OpacityMask = fade, ClipToBounds = true };   // jusqu'au bord bas du widget
        fire = new Fire(flames, FireStyle.Gauge);
        Root.Children.Add(flames);
        Root.Children.Add(face);
    }

    readonly Fire fire;

    /// <summary>0 = éteint, 1 = juste trop chaud, 2 = beaucoup trop chaud.</summary>
    public void SetFire(double intensity) => fire.Set(intensity);

    public bool Visible
    {
        set => Root.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public object? ToolTip
    {
        set => Root.ToolTip = value;
    }

    /// <param name="fraction">remplissage de l'anneau, 0 à 1</param>
    public void Set(string text, string unitText, Brush valueBrush, double fraction, Brush ringBrush, string? subText)
    {
        if (value.Text != text) value.Text = text;
        if (unit.Text != unitText) unit.Text = unitText;
        value.Foreground = valueBrush;
        ring.Fill = ringBrush;
        sub.Text = subText ?? "";
        sub.Visibility = subText == null ? Visibility.Collapsed : Visibility.Visible;

        fraction = Math.Clamp(fraction, 0, 1);
        if (Math.Abs(fraction - shown) < 0.004) return;
        shown = fraction;
        ring.BeginAnimation(ArcGauge.ValueProperty,
            new DoubleAnimation(fraction, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Trop chaud : l'anneau rougeoie en pulsant.</summary>
    public void SetHot(bool on)
    {
        if (on == hot) return;
        hot = on;
        if (!on)
        {
            ring.Effect = null;
            return;
        }
        var glow = new DropShadowEffect { Color = palette.Hot.Color, ShadowDepth = 0, BlurRadius = 18, Opacity = 0.9 };
        var pulse = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(520)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(pulse, 30);
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
        ring.Effect = glow;
    }
}
