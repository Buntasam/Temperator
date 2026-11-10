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
    public bool ShowNet { get; set; }
    public bool ShowBattery { get; set; } = true;
    public bool ShowPower { get; set; }

    /// <summary>Matériel affiché (identifiant LibreHardwareMonitor) ; null : automatique.</summary>
    public string? GpuId { get; set; }
    public string? DiskId { get; set; }
    public string? NetId { get; set; }

    /// <summary>Langue : "auto" (celle de Windows), "fr" ou "en".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Couleur d'accent du widget (#RRGGBB).</summary>
    public string Accent { get; set; } = "#4ED8C0";
    /// <summary>Jeu ou vidéo en plein écran : "compact", "hide" (masquer) ou "off".</summary>
    public string GameMode { get; set; } = "compact";

    public bool Fahrenheit { get; set; }
    /// <summary>Verre teinté qui adapte ses couleurs au fond de l'écran.</summary>
    public bool Translucent { get; set; }
    /// <summary>Aucun fond : seuls le texte et les barres flottent sur l'écran (couleurs adaptées au fond).</summary>
    public bool Transparent { get; set; }
    /// <summary>Opacité du fond seul (0 à 1), indépendante de l'opacité générale.</summary>
    public double BackgroundOpacity { get; set; } = 1.0;
    /// <summary>Affichage en jauges rondes au lieu des lignes et barres.</summary>
    public bool Gauges { get; set; } = true;
    public bool FireAnimation { get; set; } = true;
    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;
    public int IntervalMs { get; set; } = 1000;
    public bool Topmost { get; set; } = true;
    /// <summary>Repasse au-dessus dès qu'une autre fenêtre le recouvre (avec Topmost).</summary>
    public bool TopmostForce { get; set; }
    public bool Locked { get; set; }
    public bool Hidden { get; set; }

    public const string Lite = "lite", Full = "full";
    /// <summary>Mode simple (menu court) ou complet (toutes les options). null : pas encore choisi (fenêtre de bienvenue).</summary>
    public string? Mode { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsLite => Mode != Full;

    /// <summary>Clics traversants : la souris passe à travers le widget (Ctrl enfoncé pour le saisir).</summary>
    public bool ClickThrough { get; set; }
    /// <summary>Raccourci global pour afficher/masquer ("" = aucun).</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+T";

    /// <summary>Notification Windows quand un composant reste trop chaud plus de 30 s.</summary>
    public bool HeatAlerts { get; set; } = true;
    /// <summary>Seuils « trop chaud » en °C (rouge, flammes, alertes). L'orange commence un peu en dessous.</summary>
    public double CpuHot { get; set; } = 85;
    public double GpuHot { get; set; } = 87;
    public double DiskHot { get; set; } = 60;

    /// <summary>Mises à jour : "auto" (installe toute seule), "notify" (prévient), "off".</summary>
    public string UpdateMode { get; set; } = "auto";
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>Pour annoncer « Temperator a été mis à jour » au premier lancement d'une nouvelle version.</summary>
    public string? LastRunVersion { get; set; }

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
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.0, 1.0);
        IntervalMs = Math.Clamp(IntervalMs, 250, 10000);
        CpuHot = Math.Clamp(CpuHot, 60, 105);
        GpuHot = Math.Clamp(GpuHot, 60, 100);
        DiskHot = Math.Clamp(DiskHot, 40, 80);
        if (UpdateMode is not ("auto" or "notify" or "off")) UpdateMode = "auto";
        if (GameMode is not ("compact" or "hide" or "off")) GameMode = "compact";
        if (Language is not ("auto" or "fr" or "en")) Language = "auto";
        if (Accent is null || !System.Text.RegularExpressions.Regex.IsMatch(Accent, "^#[0-9A-Fa-f]{6}$")) Accent = "#4ED8C0";
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
