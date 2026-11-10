using System;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Temperator;

/// <summary>
/// Couleurs du widget. Les pinceaux ne sont pas figés : passer d'une palette à l'autre
/// anime leurs couleurs sur place, et tout ce qui les utilise suit.
/// </summary>
sealed class Palette
{
    public readonly SolidColorBrush Text = new(), Soft = new(), Muted = new(), Track = new(), Line = new(),
                                    Accent = new(), Warn = new(), Hot = new(), Tint = new();

    /// <summary>Couleur du halo derrière le texte (mode translucide) : l'inverse du texte.</summary>
    public Color Halo { get; private set; }
    public bool IsLight { get; private set; }

    readonly record struct Set(Color Text, Color Soft, Color Muted, Color Track, Color Line, Color Accent, Color Warn, Color Hot, Color Halo);

    // fond sombre : texte clair, couleurs vives
    static readonly Set Dark = new(C("#F1F3F6"), C("#C9CFD8"), C("#9AA3AF"), C("#2EF1F3F6"), C("#26F1F3F6"),
                                   C("#4ED8C0"), C("#FFB547"), C("#FF5A5F"), C("#000000"));
    // fond clair : texte foncé, couleurs assombries pour garder le contraste
    static readonly Set Light = new(C("#11151B"), C("#28303A"), C("#475060"), C("#2B000000"), C("#38000000"),
                                    C("#07826B"), C("#AD5C00"), C("#CC1F27"), C("#FFFFFF"));

    public Palette() => Use(false, false);

    public void Use(bool light, bool animate)
    {
        IsLight = light;
        var s = light ? Light : Dark;
        Halo = s.Halo;
        To(Text, s.Text, animate); To(Soft, s.Soft, animate); To(Muted, s.Muted, animate);
        To(Track, s.Track, animate); To(Line, s.Line, animate);
        To(Accent, s.Accent, animate); To(Warn, s.Warn, animate); To(Hot, s.Hot, animate);
    }

    public void SetTint(Color c, bool animate) => To(Tint, c, animate);

    public SolidColorBrush ForTemp(float? celsius, float warn, float hot) =>
        celsius is not float c ? Muted : c >= hot ? Hot : c >= warn ? Warn : Accent;

    public static void To(SolidColorBrush b, Color c, bool animate)
    {
        if (!animate)
        {
            b.BeginAnimation(SolidColorBrush.ColorProperty, null);
            b.Color = c;
            return;
        }
        b.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(c, TimeSpan.FromMilliseconds(450)) { EasingFunction = new QuadraticEase() });
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
