using System;
using System.IO;
using System.Text.Json;

namespace Temperator;

/// <summary>Réglages du widget, enregistrés dans %ProgramData%\Temperator\settings.json.</summary>
public sealed class AppSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }

    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowDisk { get; set; } = true;
    public bool ShowFan { get; set; }

    public bool Fahrenheit { get; set; }
    /// <summary>Verre teinté qui adapte ses couleurs au fond de l'écran.</summary>
    public bool Translucent { get; set; }
    /// <summary>Aucun fond : seuls le texte et les barres flottent sur l'écran (couleurs adaptées au fond).</summary>
    public bool Transparent { get; set; }
    public bool FireAnimation { get; set; } = true;
    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;
    public int IntervalMs { get; set; } = 1000;
    public bool Topmost { get; set; } = true;
    /// <summary>Repasse au-dessus dès qu'une autre fenêtre le recouvre (avec Topmost).</summary>
    public bool TopmostForce { get; set; }
    public bool Locked { get; set; }
    public bool Hidden { get; set; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Dossier des réglages et du journal : ProgramData (créé par l'installeur), sinon AppData.</summary>
    public static string Folder { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Temperator");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        foreach (var dir in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Temperator"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temperator"),
        })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".ecriture");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                Folder = dir;
                break;
            }
            catch { }
        }

        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()).Sanitized();
        }
        catch (Exception ex) { App.Log(ex); }
        return new AppSettings();
    }

    AppSettings Sanitized()
    {
        Scale = Math.Clamp(Scale, 0.5, 2.5);
        Opacity = Math.Clamp(Opacity, 0.3, 1.0);
        IntervalMs = Math.Clamp(IntervalMs, 250, 10000);
        return this;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { App.Log(ex); }
    }
}
