using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Temperator;

/// <summary>Flammes et braises qui montent du bas du widget quand un composant est trop chaud.</summary>
sealed class Fire
{
    // goutte de flamme dans un carré 1×1, pointe en haut
    static readonly Geometry Flame = Freeze(Geometry.Parse(
        "M 0.5,1 C 0.08,0.97 0,0.66 0.2,0.43 C 0.3,0.32 0.36,0.16 0.48,0 C 0.58,0.2 0.86,0.36 0.88,0.62 C 0.93,0.86 0.8,0.98 0.5,1 Z"));

    static readonly Brush BackFlame = Gradient((0, "#FFFF3D1F"), (0.45, "#D9FF6A1F"), (1, "#00FF2A10"));
    static readonly Brush FrontFlame = Gradient((0, "#FFFFF3C4"), (0.3, "#FFFFC23D"), (0.65, "#CCFF7A1F"), (1, "#00FF4A1A"));
    static readonly Brush Glow = Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(0x00, 0xFF, 0x46, 0x14), 0),
        new(Color.FromArgb(0x80, 0xFF, 0x46, 0x14), 1),
    }, 90));
    static readonly Brush Ember = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x5A)));

    const int Fps = 30;   // assez fluide, et léger pour le processeur
    readonly Canvas layer;
    readonly Random rnd = new();
    double builtWidth = -1, builtHeight = -1, target = -1;
    bool burning;

    public Fire(Canvas layer)
    {
        this.layer = layer;
        layer.IsHitTestVisible = false;
        layer.Opacity = 0;
        layer.Visibility = Visibility.Hidden;   // Hidden (pas Collapsed) : garde sa taille pour construire les flammes
        layer.SizeChanged += (_, _) => { if (burning) Rebuild(); };
    }

    public bool Burning => burning;

    /// <summary>0 = éteint, 1 = juste trop chaud, 2 = beaucoup trop chaud.</summary>
    public void Set(double intensity)
    {
        if (intensity <= 0)
        {
            if (burning) Stop();
            return;
        }
        if (!burning)
        {
            burning = true;
            Rebuild();
            layer.Visibility = Visibility.Visible;
        }
        FadeTo(Math.Clamp(0.65 + 0.35 * (intensity - 1), 0.65, 1));
    }

    void Rebuild()
    {
        double w = layer.ActualWidth, h = layer.ActualHeight;
        if (w < 20 || h < 20 || (Math.Abs(w - builtWidth) < 1 && Math.Abs(h - builtHeight) < 1)) return;
        builtWidth = w;
        builtHeight = h;
        layer.Children.Clear();

        var glow = new Rectangle { Width = w, Height = h * 0.8, Fill = Glow };
        Canvas.SetBottom(glow, 0);
        layer.Children.Add(glow);
        Loop(glow, UIElement.OpacityProperty, 0.55, 1, 0.9, 1.4);

        double tall = Math.Min(h * 0.62, 64);
        AddRow(w, tall, BackFlame, spacing: 15);
        AddRow(w, tall * 0.62, FrontFlame, spacing: 11);
        for (int i = 0; i < 14; i++) AddEmber(w, h);
    }

    void AddRow(double w, double height, Brush fill, double spacing)
    {
        for (double x = -4; x < w + 4; x += spacing * (0.75 + rnd.NextDouble() * 0.5))
        {
            double fh = height * (0.55 + rnd.NextDouble() * 0.45), fw = fh * (0.42 + rnd.NextDouble() * 0.12);
            var scale = new ScaleTransform();
            var skew = new SkewTransform();
            var flame = new Path
            {
                Data = Flame, Stretch = Stretch.Fill, Width = fw, Height = fh, Fill = fill,
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = new TransformGroup { Children = { scale, skew } },
            };
            Canvas.SetLeft(flame, x - fw / 2);
            Canvas.SetBottom(flame, -fh * 0.1);
            layer.Children.Add(flame);

            Loop(scale, ScaleTransform.ScaleYProperty, 0.72, 1.18, 0.26, 0.6);
            Loop(scale, ScaleTransform.ScaleXProperty, 0.88, 1.08, 0.3, 0.7);
            Loop(skew, SkewTransform.AngleXProperty, -8, 8, 0.5, 1.1);
            Loop(flame, UIElement.OpacityProperty, 0.7, 1, 0.2, 0.5);
        }
    }

    void AddEmber(double w, double h)
    {
        double size = 1.5 + rnd.NextDouble() * 2.2;
        var move = new TranslateTransform();
        var ember = new Ellipse { Width = size, Height = size, Fill = Ember, Opacity = 0, RenderTransform = move };
        Canvas.SetLeft(ember, rnd.NextDouble() * w);
        Canvas.SetBottom(ember, 4 + rnd.NextDouble() * 10);
        layer.Children.Add(ember);

        var dur = TimeSpan.FromSeconds(1.1 + rnd.NextDouble() * 1.5);
        var begin = TimeSpan.FromSeconds(rnd.NextDouble() * 2.2);
        Run(move, TranslateTransform.YProperty, new DoubleAnimation(0, -h * (0.45 + rnd.NextDouble() * 0.5), dur) { BeginTime = begin });
        Run(move, TranslateTransform.XProperty, new DoubleAnimation(0, (rnd.NextDouble() - 0.5) * 26, dur) { BeginTime = begin });
        Run(ember, UIElement.OpacityProperty, new DoubleAnimation(1, 0, dur) { BeginTime = begin, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
    }

    /// <summary>Va-et-vient sans fin entre deux valeurs, durée et départ au hasard.</summary>
    void Loop(IAnimatable target, DependencyProperty property, double from, double to, double minSeconds, double maxSeconds)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(minSeconds + rnd.NextDouble() * (maxSeconds - minSeconds)))
        {
            AutoReverse = true,
            BeginTime = TimeSpan.FromSeconds(rnd.NextDouble() * 0.4),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Run(target, property, a);
    }

    static void Run(IAnimatable target, DependencyProperty property, DoubleAnimation a)
    {
        a.RepeatBehavior = RepeatBehavior.Forever;
        Timeline.SetDesiredFrameRate(a, Fps);
        target.BeginAnimation(property, a);
    }

    void FadeTo(double opacity)
    {
        if (Math.Abs(opacity - target) < 0.01) return;
        target = opacity;
        layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(600)));
    }

    void Stop()
    {
        burning = false;
        target = -1;
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(800));
        a.Completed += (_, _) =>
        {
            if (burning) return;
            layer.Children.Clear();   // plus aucune animation qui tourne
            builtWidth = builtHeight = -1;
            layer.Visibility = Visibility.Hidden;
        };
        layer.BeginAnimation(UIElement.OpacityProperty, a);
    }

    static Brush Gradient(params (double Offset, string Color)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0.5, 1), EndPoint = new Point(0.5, 0) };
        foreach (var (offset, color) in stops) b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), offset));
        return Freeze(b);
    }

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
