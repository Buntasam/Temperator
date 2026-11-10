using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;

namespace TemperatorSetup
{
    /// <summary>
    /// Temperator-Setup.exe
    ///   (sans option)          interface d'installation
    ///   /uninstall             interface de désinstallation (Paramètres > Applications)
    ///   /S                     sans interface : /noautostart /nodesktop /nolaunch /keepsettings /name X
    ///   /update                mise à jour (garde les choix déjà faits), utilisée par la mise à jour automatique
    ///   /preview dossier       rend chaque écran en PNG, sans rien installer
    ///   /lang fr|en            langue de l'interface (par défaut : celle de Windows)
    /// Les options s'écrivent aussi avec un tiret (-Silent, -Uninstall, -NoLaunch…).
    /// </summary>
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var o = new Options();
            bool silent = false, uninstall = false, fromTemp = false;
            string preview = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].TrimStart('/', '-').ToLowerInvariant())
                {
                    case "s": case "silent": case "quiet": silent = true; break;
                    case "uninstall": uninstall = true; break;
                    case "update": o.Update = true; break;
                    case "noautostart": o.AutoStart = false; break;
                    case "nodesktop": o.Desktop = false; break;
                    case "nolaunch": o.Launch = false; break;
                    case "keepsettings": o.KeepSettings = true; break;
                    case "fromtemp": fromTemp = true; break;
                    case "name" when i + 1 < args.Length: o.Name = args[++i]; break;
                    case "preview" when i + 1 < args.Length: preview = args[++i]; break;
                    case "lang" when i + 1 < args.Length: S.Fr = args[++i].StartsWith("fr", StringComparison.OrdinalIgnoreCase); break;
                }
            }

            if (preview != null)
            {
                new Application();
                new SetupWindow(o, false, preview: true).RenderPreview(preview);
                return 0;
            }

            // Désinstaller depuis le dossier installé : on se copie dans %TEMP% pour pouvoir supprimer ce dossier.
            string self = Assembly.GetExecutingAssembly().Location;
            if (uninstall && !fromTemp && self.StartsWith(Engine.Dir(o.Name), StringComparison.OrdinalIgnoreCase))
            {
                string copy = Path.Combine(Path.GetTempPath(), "Temperator-Uninstall.exe");
                File.Copy(self, copy, true);
                Process.Start(new ProcessStartInfo(copy, string.Join(" ", Quote(args)) + " /fromtemp") { UseShellExecute = false });
                return 0;
            }

            try
            {
                if (silent || o.Update) return RunSilent(o, uninstall);
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.Run(new SetupWindow(o, uninstall));
                return 0;
            }
            finally
            {
                if (fromTemp) DeleteSelfLater(self);
            }
        }

        static int RunSilent(Options o, bool uninstall)
        {
            string log = Path.Combine(Path.GetTempPath(), "temperator-setup.log");
            var report = new Log(log);
            try
            {
                report.Write($"v{Engine.Version} {(uninstall ? "désinstallation" : o.Update ? "mise à jour" : "installation")} '{o.Name}' auto={o.AutoStart} bureau={o.Desktop} lancer={o.Launch}");
                if (uninstall) Engine.Uninstall(o, report);
                else Engine.Install(o, report);
                return 0;
            }
            catch (Exception e)
            {
                report.Write("ERREUR " + e.Message);
                return 1;
            }
        }

        sealed class Log : IProgress<Step>
        {
            readonly string file;
            public Log(string file) { this.file = file; }
            public void Report(Step s) => Write($"{s.Value * 100,3:0}% {s.Text}");
            public void Write(string line)
            {
                try { File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss} {line}\r\n", Encoding.UTF8); } catch { }
            }
        }

        static void DeleteSelfLater(string exe)
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{exe}\"")
                { UseShellExecute = false, CreateNoWindow = true });
            }
            catch { }
        }

        static string[] Quote(string[] args)
        {
            var q = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
                q[i] = args[i].IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? args[i] : "\"" + args[i].Replace("\"", "\\\"") + "\"";
            return q;
        }
    }
}
