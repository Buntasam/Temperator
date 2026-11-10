using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Temperator;

/// <summary>Réglages du widget, enregistrés dans %ProgramData%\Temperator\settings.json.</summary>
public sealed class AppSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }

    // ------------------------------------------------------------------ ce qui est affiché

    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowDisk { get; set; } = true;
    public bool ShowFan { get; set; }
    public bool ShowNet { get; set; }
    public bool ShowBattery { get; set; } = true;
    public bool ShowPower { get; set; }
    /// <summary>Images par seconde du jeu en plein écran (jeux DirectX).</summary>
    public bool ShowFps { get; set; } = true;

    /// <summary>Ordre des éléments dans le widget (clés de <see cref="MetricKeys"/>).</summary>
    public List<string> Order { get; set; } = MetricKeys.All.ToList();

    /// <summary>Matériel affiché (identifiant LibreHardwareMonitor) ; null : automatique.</summary>
    public string? GpuId { get; set; }
    public string? DiskId { get; set; }
    public string? NetId { get; set; }

    // ------------------------------------------------------------------ apparence

    public const string Gauges = "gauges", Rows = "rows", Bar = "bar";
    /// <summary>Disposition : jauges rondes, lignes et barres, ou barre fine.</summary>
    public string Layout { get; set; } = Gauges;
    /// <summary>Ancien réglage (versions ≤ 1.5) : converti en <see cref="Layout"/> au chargement.</summary>
    [JsonPropertyName("Gauges"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyGauges { get; set; }

    public const string Dark = "dark", Light = "light", FollowWindows = "system";
    /// <summary>Couleurs du fond opaque : sombre, clair, ou comme Windows.</summary>
    public string Theme { get; set; } = Dark;

    /// <summary>Langue : "auto" (celle de Windows), "fr" ou "en".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Couleur d'accent du widget (#RRGGBB).</summary>
    public string Accent { get; set; } = "#4ED8C0";
    public bool Fahrenheit { get; set; }
    /// <summary>Verre teinté qui adapte ses couleurs au fond de l'écran.</summary>
    public bool Translucent { get; set; }
    /// <summary>Aucun fond : seuls le texte et les barres flottent sur l'écran (couleurs adaptées au fond).</summary>
    public bool Transparent { get; set; }
    /// <summary>Opacité du fond seul (0 à 1), indépendante de l'opacité générale.</summary>
    public double BackgroundOpacity { get; set; } = 1.0;
    public bool FireAnimation { get; set; } = true;
    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;
    public int IntervalMs { get; set; } = 1000;

    // ------------------------------------------------------------------ profil « Jeu » : jeu ou vidéo en plein écran

    public const string GameProfile = "profile", GameHide = "hide", GameOff = "off";
    /// <summary>Plein écran : profil « Jeu », masquer, ou ne rien changer.</summary>
    public string GameMode { get; set; } = GameProfile;
    /// <summary>Éléments affichés pendant un jeu.</summary>
    public List<string> GameShow { get; set; } = new() { MetricKeys.Cpu, MetricKeys.Gpu, MetricKeys.Fps };
    public double GameScale { get; set; } = 0.85;
    public double GameBackgroundOpacity { get; set; } = 0.35;
    /// <summary>Disposition pendant un jeu (null : la même qu'au bureau).</summary>
    public string? GameLayout { get; set; }

    // ------------------------------------------------------------------ comportement

    public bool Topmost { get; set; } = true;
    /// <summary>Repasse au-dessus dès qu'une autre fenêtre le recouvre (avec Topmost).</summary>
    public bool TopmostForce { get; set; }
    public bool Locked { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Clics traversants : la souris passe à travers le widget (Ctrl enfoncé pour le saisir).</summary>
    public bool ClickThrough { get; set; }
    /// <summary>Raccourci global pour afficher/masquer ("" = aucun).</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+T";

    public const string Lite = "lite", Full = "full";
    /// <summary>Mode simple (menu court) ou complet (toutes les options). null : pas encore choisi (fenêtre de bienvenue).</summary>
    public string? Mode { get; set; }
    [JsonIgnore] public bool IsLite => Mode != Full;

    // ------------------------------------------------------------------ alertes et mises à jour

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

    // ------------------------------------------------------------------ fichier

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
            if (File.Exists(FilePath)) return Parse(File.ReadAllText(FilePath));
        }
        catch (Exception ex) { App.Log(ex); }
        return new AppSettings();
    }

    /// <summary>Lit des réglages JSON (anciens compris) et remet chaque valeur dans des limites raisonnables.</summary>
    public static AppSettings Parse(string json) =>
        (JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings()).Sanitized();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    internal AppSettings Sanitized()
    {
        // versions ≤ 1.5 : un booléen « Gauges », et le mode jeu s'appelait « compact »
        if (LegacyGauges is bool gauges) Layout = gauges ? Gauges : Rows;
        LegacyGauges = null;
        if (GameMode == "compact") GameMode = GameProfile;

        Scale = Math.Clamp(Scale, 0.5, 2.5);
        GameScale = Math.Clamp(GameScale, 0.5, 2.5);
        Opacity = Math.Clamp(Opacity, 0.3, 1.0);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.0, 1.0);
        GameBackgroundOpacity = Math.Clamp(GameBackgroundOpacity, 0.0, 1.0);
        IntervalMs = Math.Clamp(IntervalMs, 250, 10000);
        CpuHot = Math.Clamp(CpuHot, 60, 105);
        GpuHot = Math.Clamp(GpuHot, 60, 100);
        DiskHot = Math.Clamp(DiskHot, 40, 80);
        if (Layout is not (Gauges or Rows or Bar)) Layout = Gauges;
        if (GameLayout is not (null or Gauges or Rows or Bar)) GameLayout = null;
        if (Theme is not (Dark or Light or FollowWindows)) Theme = Dark;
        if (UpdateMode is not ("auto" or "notify" or "off")) UpdateMode = "auto";
        if (GameMode is not (GameProfile or GameHide or GameOff)) GameMode = GameProfile;
        if (Language is not ("auto" or "fr" or "en")) Language = "auto";
        if (Accent is null || !Regex.IsMatch(Accent, "^#[0-9A-Fa-f]{6}$")) Accent = "#4ED8C0";

        // ordre : chaque élément connu une seule fois, les nouveaux ajoutés à la fin
        Order = (Order ?? new()).Where(MetricKeys.All.Contains).Distinct().ToList();
        Order.AddRange(MetricKeys.All.Where(k => !Order.Contains(k)));
        GameShow = (GameShow ?? new()).Where(MetricKeys.All.Contains).Distinct().ToList();
        return this;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, ToJson());
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ------------------------------------------------------------------ affichage de chaque élément

    public bool Shows(string key) => key switch
    {
        MetricKeys.Cpu => ShowCpu,
        MetricKeys.Gpu => ShowGpu,
        MetricKeys.Fps => ShowFps,
        MetricKeys.Ram => ShowRam,
        MetricKeys.Disk => ShowDisk,
        MetricKeys.Fan => ShowFan,
        MetricKeys.Net => ShowNet,
        MetricKeys.Battery => ShowBattery,
        MetricKeys.Power => ShowPower,
        _ => false,
    };

    public void SetShows(string key, bool value)
    {
        switch (key)
        {
            case MetricKeys.Cpu: ShowCpu = value; break;
            case MetricKeys.Gpu: ShowGpu = value; break;
            case MetricKeys.Fps: ShowFps = value; break;
            case MetricKeys.Ram: ShowRam = value; break;
            case MetricKeys.Disk: ShowDisk = value; break;
            case MetricKeys.Fan: ShowFan = value; break;
            case MetricKeys.Net: ShowNet = value; break;
            case MetricKeys.Battery: ShowBattery = value; break;
            case MetricKeys.Power: ShowPower = value; break;
        }
    }
}

/// <summary>Les éléments que le widget sait afficher.</summary>
public static class MetricKeys
{
    public const string Cpu = "cpu", Gpu = "gpu", Fps = "fps", Ram = "ram", Disk = "disk", Fan = "fan", Net = "net", Battery = "battery", Power = "power";
    public static readonly string[] All = { Cpu, Gpu, Fps, Ram, Disk, Fan, Net, Battery, Power };

    public static string Name(string key) => key switch
    {
        Cpu => L.T("Processeur (CPU)", "Processor (CPU)"),
        Gpu => L.T("Carte graphique (GPU)", "Graphics card (GPU)"),
        Fps => L.T("Images par seconde (FPS)", "Frames per second (FPS)"),
        Ram => L.T("Mémoire (RAM)", "Memory (RAM)"),
        Disk => L.T("Disque", "Drive"),
        Fan => L.T("Ventilateur", "Fan"),
        Net => L.T("Réseau", "Network"),
        Battery => L.T("Batterie", "Battery"),
        Power => L.T("Consommation", "Power draw"),
        _ => key,
    };
}
