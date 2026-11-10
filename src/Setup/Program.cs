using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace TemperatorSetup
{
    /// <summary>
    /// Extrait l'installeur et l'application dans un dossier temporaire, lance l'installeur
    /// (Windows PowerShell, sans fenêtre de console) et attend qu'il se termine.
    /// Les arguments sont transmis à l'installeur : /S ou /silent = -Silent, /uninstall = -Uninstall.
    /// </summary>
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "Temperator-Setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(dir);
                Extract("Installer.ps1", dir);
                Extract("Temperator.zip", dir);

                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                string script = Path.Combine(dir, "Installer.ps1");
                string arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Quote(script) +
                                   string.Concat(args.Select(a => " " + Quote(Translate(a))));

                var psi = new ProcessStartInfo(powershell, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = dir,
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception e)
            {
                MessageBox.Show("L'installation de Temperator n'a pas pu démarrer :\n\n" + e.Message,
                    "Temperator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static void Extract(string name, string dir)
        {
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var target = File.Create(Path.Combine(dir, name)))
            {
                if (source == null) throw new InvalidOperationException(name + " manque dans l'installeur.");
                source.CopyTo(target);
            }
        }

        static string Translate(string arg)
        {
            switch (arg.ToLowerInvariant())
            {
                case "/s":
                case "/silent":
                case "/quiet":
                    return "-Silent";
                case "/uninstall":
                    return "-Uninstall";
                default:
                    return arg;
            }
        }

        static string Quote(string a) =>
            a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
    }
}
