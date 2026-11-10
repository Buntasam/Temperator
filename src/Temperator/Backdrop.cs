using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using WColor = System.Windows.Media.Color;

namespace Temperator;

/// <summary>
/// Regarde l'écran juste autour du widget (quatre fines bandes collées à ses bords, donc jamais le widget lui-même) :
/// couleur moyenne, luminosité et « agitation » du fond.
/// </summary>
static class Backdrop
{
    /// <param name="Luma">0 = noir, 1 = blanc.</param>
    /// <param name="Busy">0 = fond uni, 1 = fond très contrasté (photo, texte…).</param>
    public readonly record struct Sample(WColor Average, double Luma, double Busy);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    public static Sample? Around(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var r) ? Around(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)) : null;

    public static Sample? Around(Rectangle w)
    {
        const int gap = 2, thick = 3;
        try
        {
            var screen = Forms.SystemInformation.VirtualScreen;
            Rectangle[] strips =
            {
                new(w.Left, w.Top - gap - thick, w.Width, thick),
                new(w.Left, w.Bottom + gap, w.Width, thick),
                new(w.Left - gap - thick, w.Top, thick, w.Height),
                new(w.Right + gap, w.Top, thick, w.Height),
            };

            long n = 0;
            double sr = 0, sg = 0, sb = 0, sl = 0, sl2 = 0;
            foreach (var strip in strips)
            {
                var s = Rectangle.Intersect(strip, screen);   // le widget collé au bord de l'écran : bande ignorée
                if (s.Width <= 0 || s.Height <= 0) continue;
                using var bmp = new Bitmap(s.Width, s.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(s.Location, Point.Empty, s.Size);
                var data = bmp.LockBits(new Rectangle(0, 0, s.Width, s.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var px = new byte[data.Stride * s.Height];
                    Marshal.Copy(data.Scan0, px, 0, px.Length);
                    for (int y = 0; y < s.Height; y++)
                        for (int x = 0; x < s.Width; x++)
                        {
                            int i = y * data.Stride + x * 4;
                            double b = px[i], g = px[i + 1], r = px[i + 2];
                            double l = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
                            sr += r; sg += g; sb += b; sl += l; sl2 += l * l; n++;
                        }
                }
                finally { bmp.UnlockBits(data); }
            }
            if (n == 0) return null;

            double mean = sl / n;
            double sd = Math.Sqrt(Math.Max(0, sl2 / n - mean * mean));
            return new Sample(WColor.FromRgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n)), mean, Math.Clamp(sd * 4, 0, 1));
        }
        catch
        {
            return null;   // écran sécurisé (UAC, verrouillage) : on garde les couleurs actuelles
        }
    }

    public static Sample Of(WColor c, double busy = 0.1) =>
        new(c, (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255, busy);
}
