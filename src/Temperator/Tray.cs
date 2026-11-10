using System;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Temperator;

/// <summary>Icône dans la zone de notification : clic = afficher/masquer, clic droit = menu.</summary>
sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon icon;
    string text = "";

    public Tray(App app)
    {
        icon = new Forms.NotifyIcon { Text = "Temperator", Icon = LoadIcon(), Visible = true };
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) app.ToggleWidget();
            else if (e.Button == Forms.MouseButtons.Right) app.ShowMenu(null);
        };
    }

    static Icon LoadIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Temperator.ico"));
            using var stream = res!.Stream;
            return new Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { return SystemIcons.Application; }
    }

    public void Update(Readings r, AppSettings s)
    {
        if (!r.Ready) return;
        string T(float? c) => c is float v ? Fmt.Temp(v, s.Fahrenheit).ToString("0") + (s.Fahrenheit ? "°F" : "°") : "—";
        string P(float? l) => l is float v ? $"{v:0} %" : "—";
        var t = $"Temperator\nCPU {T(r.CpuTemp)} · {P(r.CpuLoad)}" +
                (r.GpuName != null ? $"\nGPU {T(r.GpuTemp)} · {P(r.GpuLoad)}" : "") +
                (r.RamLoad.HasValue ? $"\nRAM {P(r.RamLoad)}" : "");
        if (t.Length > 127) t = t[..127];
        if (t == text) return;
        text = t;
        try { icon.Text = t; } catch { }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
