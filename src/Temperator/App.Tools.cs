using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Temperator;

/// <summary>Outils : export de l'historique, rapport de diagnostic, licence, liens.</summary>
public partial class App
{
    // ------------------------------------------------------------------ outils

    public void ExportHistory(Window? owner)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"temperator-{L.T("historique", "history")}-{DateTime.Now:yyyy-MM-dd-HHmm}.csv",
            Filter = L.T("Fichier CSV (*.csv)|*.csv", "CSV file (*.csv)|*.csv"),
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            History.ExportCsv(dialog.FileName);
            Notify(L.T("Historique exporté", "History exported"), dialog.FileName, onClick: () => OpenUrl(Path.GetDirectoryName(dialog.FileName)!));
        }
        catch (Exception ex)
        {
            Log(ex);
            Notify(L.T("Export impossible", "Export failed"), ex.Message, warning: true);
        }
    }

    /// <summary>Version, Windows, matériel, valeurs, réglages et dernières erreurs : à coller dans une issue GitHub.</summary>
    public void CopyDiagnostics()
    {
        var r = Last;
        string V(float? v, string unit) => v is float x ? $"{x:0.#}{unit}" : "—";
        var sb = new StringBuilder();
        sb.AppendLine($"Temperator {Version} — diagnostic {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Windows : {Fmt.OsName()} · .NET {Environment.Version} · admin={IsAdmin} · PawnIO={Native.PawnIOInstalled.Value}");
        sb.AppendLine($"Langue={Settings.Language} ({(L.Fr ? "fr" : "en")}) · mode={Settings.Mode} · affichage={Settings.Layout} · thème={Settings.Theme} · plein écran={Settings.GameMode}");
        sb.AppendLine();
        sb.AppendLine($"CPU : {r.CpuName} · {V(r.CpuTemp, " °C")} · {V(r.CpuLoad, " %")} · {V(r.CpuPower, " W")}");
        sb.AppendLine($"GPU : {r.GpuName} · {V(r.GpuTemp, " °C")} · {V(r.GpuLoad, " %")} · {V(r.GpuPower, " W")}");
        foreach (var (_, name) in r.Gpus) sb.AppendLine($"  GPU détecté : {name}");
        sb.AppendLine($"RAM : {V(r.RamUsed, " Go")} / {V(r.RamTotal, " Go")} · {V(r.RamLoad, " %")}");
        sb.AppendLine($"Disque : {r.DiskName} · {V(r.DiskTemp, " °C")} · {V(r.DiskLoad, " %")}");
        foreach (var (_, name) in r.Disks) sb.AppendLine($"  disque détecté : {name}");
        sb.AppendLine($"Réseau : {r.NetName} · ↓ {V(r.NetDown, " o/s")} · ↑ {V(r.NetUp, " o/s")} ({r.Nets.Count} cartes)");
        sb.AppendLine($"Batterie : {V(r.BatteryLevel, " %")} · {V(r.BatteryRate, " W")} · charge={r.BatteryCharging}");
        sb.AppendLine($"Ventilateur : {V(r.FanRpm, " tr/min")} · conso CPU+GPU : {V(r.TotalPower, " W")}");
        sb.AppendLine();
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings))!;
        copy.Left = copy.Top = null;
        sb.AppendLine("Réglages : " + JsonSerializer.Serialize(copy));
        try
        {
            string log = Path.Combine(AppSettings.Folder, "erreurs.log");
            if (File.Exists(log))
            {
                sb.AppendLine();
                sb.AppendLine("Dernières erreurs :");
                foreach (var line in File.ReadLines(log).TakeLast(40)) sb.AppendLine(line);
            }
        }
        catch { }

        for (int i = 0; i < 5; i++)   // presse-papiers parfois occupé par une autre application
        {
            try
            {
                Clipboard.SetText(sb.ToString());
                Notify(L.T("Rapport copié", "Report copied"), L.T("Colle-le dans une issue sur GitHub (Ctrl+V).", "Paste it into an issue on GitHub (Ctrl+V)."));
                return;
            }
            catch { Thread.Sleep(100); }
        }
        Notify(L.T("Copie impossible", "Copy failed"), L.T("Le presse-papiers est occupé. Réessaie.", "The clipboard is busy. Try again."), warning: true);
    }

    /// <summary>La licence installée avec l'application (Bloc-notes), sinon celle du dépôt GitHub.</summary>
    internal static void OpenLicense()
    {
        try
        {
            string file = Path.Combine(AppContext.BaseDirectory, "LICENSE");
            if (File.Exists(file)) Process.Start("notepad.exe", "\"" + file + "\"");
            else OpenUrl($"https://github.com/{Updater.Repo}/blob/main/LICENSE");
        }
        catch (Exception ex) { Log(ex); }
    }

    internal static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log(ex); }
    }
}
