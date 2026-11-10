using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Temperator;

/// <summary>
/// Mises à jour depuis les releases GitHub : vérifie une fois par jour, télécharge Temperator-Setup-x.y.z.exe,
/// contrôle son empreinte SHA-256 (SHA256SUMS.txt publié avec la release), puis l'installe en silence.
/// Temperator tourne déjà en administrateur : aucune demande UAC.
/// </summary>
sealed class Updater
{
    public const string Repo = "Buntasam/Temperator";
    public const string Auto = "auto", NotifyOnly = "notify", Off = "off";

    static readonly HttpClient Http = CreateClient();
    readonly App app;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromHours(1) };
    string? setupUrl, sumsUrl, setupName;
    bool busy;

    /// <summary>Version plus récente trouvée (ou null).</summary>
    public string? Available { get; private set; }

    public Updater(App app)
    {
        this.app = app;
        timer.Tick += (_, _) => _ = CheckIfDue();
        CleanUp();
    }

    public void Start()
    {
        // première vérification un peu après le démarrage de Windows, puis toutes les heures si plus de 20 h ont passé
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        first.Tick += (_, _) => { first.Stop(); _ = CheckIfDue(); };
        first.Start();
        timer.Start();
    }

    async Task CheckIfDue()
    {
        if (app.Settings.UpdateMode == Off) return;
        if (app.Settings.LastUpdateCheck is DateTime last && DateTime.Now - last < TimeSpan.FromHours(20)) return;
        await Check(manual: false);
    }

    public async Task Check(bool manual)
    {
        if (busy) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            app.Settings.LastUpdateCheck = DateTime.Now;
            app.Settings.Save();

            string tag = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var latest) || !Version.TryParse(App.Version, out var current) || latest <= current)
            {
                Available = null;
                if (manual) app.Notify(L.T("Temperator est à jour", "Temperator is up to date"), L.T($"Tu as la dernière version ({App.Version}).", $"You have the latest version ({App.Version})."));
                return;
            }

            setupUrl = sumsUrl = setupName = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (Regex.IsMatch(name, @"^Temperator-Setup-[\d.]+\.exe$", RegexOptions.IgnoreCase)) { setupUrl = url; setupName = name; }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sumsUrl = url;
            }
            if (setupUrl == null) return;
            Available = latest.ToString(3);

            if (app.Settings.UpdateMode == Auto || manual) await Install();
            else app.Notify(L.T("Mise à jour disponible", "Update available"), L.T($"Temperator {Available} est disponible. Clique ici pour l'installer.", $"Temperator {Available} is available. Click here to install it."), onClick: () => _ = Install());
        }
        catch (Exception ex)
        {
            App.Log(ex);
            if (manual) app.Notify(L.T("Vérification impossible", "Check failed"), L.T("Impossible de joindre GitHub. Vérifie ta connexion internet.", "Couldn't reach GitHub. Check your internet connection."), warning: true);
        }
    }

    public async Task Install()
    {
        if (busy || setupUrl == null || setupName == null) return;
        busy = true;
        try
        {
            app.Notify(L.T("Mise à jour de Temperator", "Updating Temperator"), L.T($"Téléchargement de la version {Available}…", $"Downloading version {Available}…"));
            byte[] data = await Http.GetByteArrayAsync(setupUrl);

            // l'installeur doit correspondre à l'empreinte publiée avec la release, sinon on n'installe rien
            if (sumsUrl == null) throw new InvalidDataException(L.T("Pas d'empreinte SHA-256 publiée avec cette version : mise à jour annulée.", "No SHA-256 fingerprint published with this version: update cancelled."));
            string sums = await Http.GetStringAsync(sumsUrl);
            string? expected = sums.Split('\n')
                .Select(l => l.Trim().Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(p => p.Length >= 2 && p[1].Equals(setupName, StringComparison.OrdinalIgnoreCase))?[0];
            string actual = Convert.ToHexString(SHA256.HashData(data));
            if (expected == null || !expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("L'empreinte SHA-256 du fichier téléchargé ne correspond pas : mise à jour annulée.", "The downloaded file's SHA-256 fingerprint doesn't match: update cancelled."));

            string dir = Path.Combine(Path.GetTempPath(), "Temperator-update");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, setupName);
            await File.WriteAllBytesAsync(file, data);

            app.Notify(L.T("Mise à jour de Temperator", "Updating Temperator"), L.T($"Installation de la version {Available}. Le widget revient dans quelques secondes.", $"Installing version {Available}. The widget will be back in a few seconds."));
            // /update : garde les choix (démarrage, raccourci) ; l'installeur ferme Temperator puis le relance
            Process.Start(new ProcessStartInfo(file, "/S /update") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
            app.Notify(L.T("Mise à jour impossible", "Update failed"), ex is InvalidDataException ? ex.Message : L.T("Le téléchargement a échoué. Réessaie plus tard.", "The download failed. Try again later."), warning: true);
        }
        finally { busy = false; }
    }

    static void CleanUp()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "Temperator-update");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Temperator/{App.Version}");
        return c;
    }
}
