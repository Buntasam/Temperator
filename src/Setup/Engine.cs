using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace TemperatorSetup
{
    /// <summary>Choix de l'installation (interface ou ligne de commande).</summary>
    sealed class Options
    {
        public string Name = "Temperator";
        public bool AutoStart = true, Desktop = true, Launch = true, KeepSettings;
        /// <summary>Mise à jour automatique : garde les choix déjà faits (démarrage, bureau) et relance toujours.</summary>
        public bool Update;
    }

    /// <summary>Langue de l'installeur : celle de Windows (français, sinon anglais).</summary>
    static class S
    {
        public static bool Fr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr";
        public static string T(string fr, string en) => Fr ? fr : en;
    }

    struct Step
    {
        public double Value;
        public string Text;
        public Step(double value, string text) { Value = value; Text = text; }
    }

    /// <summary>L'installation elle-même, sans interface.</summary>
    static class Engine
    {
        public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        public static string Dir(string name) => Path.Combine(ProgramFiles, name);
        static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Temperator");
        static string MenuLnk(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), name + ".lnk");
        static string DeskLnk(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), name + ".lnk");
        static string UninstallKey(string name) => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + name;
        static string DotNet => Path.Combine(ProgramFiles, @"dotnet\dotnet.exe");
        static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

        // ------------------------------------------------------------------ ce qu'il y a déjà sur ce PC

        /// <summary>.NET Desktop Runtime 10 ou plus récent.</summary>
        public static bool HasRuntime()
        {
            if (!File.Exists(DotNet)) return false;
            try
            {
                string output = Capture(DotNet, "--list-runtimes");
                return Regex.Matches(output, @"^Microsoft\.WindowsDesktop\.App (\d+)\.", RegexOptions.Multiline)
                            .Cast<Match>().Any(m => int.Parse(m.Groups[1].Value) >= 10);
            }
            catch { return false; }
        }

        public static bool HasPawnIO()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO")) return k != null;
        }

        public static bool IsInstalled(string name) => File.Exists(Path.Combine(Dir(name), @"app\Temperator.exe"));

        // ------------------------------------------------------------------ installation

        public static void Install(Options o, IProgress<Step> report)
        {
            string dir = Dir(o.Name), app = Path.Combine(dir, "app");
            report.Report(new Step(0.03, S.T("Préparation", "Preparing")));
            if (!Environment.Is64BitOperatingSystem || Environment.OSVersion.Version.Major < 10)
                throw new InvalidOperationException(S.T("Temperator nécessite Windows 10 ou 11 en 64 bits.", "Temperator requires 64-bit Windows 10 or 11."));

            if (o.Update)   // on garde les choix de l'installation précédente
            {
                o.AutoStart = TaskHasLogonTrigger(o.Name);
                o.Desktop = File.Exists(DeskLnk(o.Name));
                o.Launch = true;
            }
            StopTemperator(null);

            if (!HasRuntime())
            {
                report.Report(new Step(0.08, S.T("Installation de .NET 10 (Microsoft, environ 57 Mo)", "Installing .NET 10 (Microsoft, about 57 MB)")));
                InstallRuntime();
                if (!HasRuntime())
                    throw new InvalidOperationException(S.T("Impossible d'installer .NET 10. Vérifie ta connexion internet, ou installe « .NET Desktop Runtime 10 (x64) » depuis dotnet.microsoft.com, puis relance l'installeur.",
                                                          "Couldn't install .NET 10. Check your internet connection, or install \".NET Desktop Runtime 10 (x64)\" from dotnet.microsoft.com, then run the installer again."));
            }

            report.Report(new Step(0.45, S.T("Copie des fichiers", "Copying files")));
            if (Directory.Exists(app)) DeleteDirectory(app);   // pas de vieilles DLL
            Directory.CreateDirectory(app);
            using (var zip = new ZipArchive(Payload(), ZipArchiveMode.Read)) zip.ExtractToDirectory(app);

            // une copie de l'installeur sert à désinstaller (Paramètres > Applications) et à réinstaller
            string self = Assembly.GetExecutingAssembly().Location, copy = Path.Combine(dir, "Temperator-Setup.exe");
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase)) File.Copy(self, copy, true);
            foreach (var old in new[] { "Temperator-Setup.ps1", "Temperator.zip" })   // restes de l'ancien installeur PowerShell
                TryDelete(Path.Combine(dir, old));

            Directory.CreateDirectory(DataDir);
            GrantUsersModify(DataDir);   // réglages modifiables par tous

            report.Report(new Step(0.6, S.T("Démarrage et raccourcis", "Startup and shortcuts")));
            string user = ConsoleUser();
            RegisterTask(o, user, Path.Combine(app, "Temperator.exe"), app);
            CreateShortcut(MenuLnk(o.Name), dir, o.Name);
            if (o.Desktop) CreateShortcut(DeskLnk(o.Name), dir, o.Name);
            else TryDelete(DeskLnk(o.Name));
            RegisterUninstall(o, dir, app);

            if (!HasPawnIO())
            {
                report.Report(new Step(0.7, S.T("Installation du pilote PawnIO (environ 3 Mo)", "Installing the PawnIO driver (about 3 MB)")));
                InstallPawnIO();
                if (!HasPawnIO())
                    throw new InvalidOperationException(S.T("Temperator est installé, mais le pilote PawnIO (obligatoire) n'a pas pu s'installer. Vérifie ta connexion internet et relance l'installeur, ou installe PawnIO depuis pawnio.eu.",
                                                          "Temperator is installed, but the PawnIO driver (required) couldn't be installed. Check your internet connection and run the installer again, or install PawnIO from pawnio.eu."));
            }

            if (o.Launch)
            {
                report.Report(new Step(0.95, S.T("Lancement de Temperator", "Launching Temperator")));
                Run(Path.Combine(System32, "schtasks.exe"), $"/run /tn \"{o.Name}\"");
            }
            report.Report(new Step(1, S.T("Terminé", "Done")));
        }

        public static void Uninstall(Options o, IProgress<Step> report)
        {
            string dir = Dir(o.Name);
            report.Report(new Step(0.15, S.T("Arrêt de Temperator", "Stopping Temperator")));
            StopTemperator(Path.Combine(dir, "app"));
            report.Report(new Step(0.45, S.T("Suppression du démarrage et des raccourcis", "Removing startup and shortcuts")));
            Run(Path.Combine(System32, "schtasks.exe"), $"/delete /tn \"{o.Name}\" /f");
            TryDelete(MenuLnk(o.Name));
            TryDelete(DeskLnk(o.Name));
            Registry.LocalMachine.DeleteSubKeyTree(UninstallKey(o.Name), false);
            if (!o.KeepSettings) TryDeleteDirectory(DataDir);
            report.Report(new Step(0.75, S.T("Suppression des fichiers", "Removing files")));
            TryDeleteDirectory(dir);
            report.Report(new Step(1, S.T("Terminé", "Done")));
        }

        // ------------------------------------------------------------------ étapes

        static Stream Payload() =>
            Assembly.GetExecutingAssembly().GetManifestResourceStream("Temperator.zip")
            ?? throw new InvalidOperationException(S.T("L'application manque dans l'installeur. Télécharge à nouveau Temperator-Setup.exe.", "The app is missing from the installer. Download Temperator-Setup.exe again."));

        /// <summary>Ferme Temperator : d'abord poliment (il enregistre ses réglages et retire son icône), sinon de force.</summary>
        static void StopTemperator(string onlyFrom)
        {
            if (EventWaitHandle.TryOpenExisting(@"Global\Temperator.Quit", out var quit))
            {
                using (quit) quit.Set();
                for (int i = 0; i < 25 && Process.GetProcessesByName("Temperator").Length > 0; i++) Thread.Sleep(150);
            }
            foreach (var p in Process.GetProcessesByName("Temperator"))
            {
                try
                {
                    if (onlyFrom == null || p.MainModule.FileName.StartsWith(onlyFrom, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch { }
            }
        }

        /// <summary>.NET 10 Desktop Runtime (Microsoft) : winget, sinon le lien officiel de Microsoft.</summary>
        static void InstallRuntime()
        {
            Winget("Microsoft.DotNet.DesktopRuntime.10");
            if (HasRuntime()) return;
            string f = Path.Combine(Path.GetTempPath(), "temperator-dotnet10.exe");
            try
            {
                Download("https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe", f);
                Run(f, "/install /quiet /norestart");
            }
            catch { }
            finally { TryDelete(f); }
        }

        /// <summary>Pilote PawnIO (namazso, signé) : winget, sinon son installeur officiel.</summary>
        static void InstallPawnIO()
        {
            Winget("namazso.PawnIO");
            if (HasPawnIO()) return;
            string f = Path.Combine(Path.GetTempPath(), "temperator-pawnio.exe");
            try
            {
                Download("https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe", f);
                Run(f, "-install -silent");
                if (!HasPawnIO()) Run(f, "", hidden: false);   // dernier recours : son installeur à l'écran
            }
            catch { }
            finally { TryDelete(f); }
        }

        static void Winget(string id)
        {
            try { Run("winget.exe", $"install --id {id} -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity"); }
            catch { }   // winget absent : on passe au téléchargement direct
        }

        static void Download(string url, string file)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "Temperator-Setup/" + Version);
                wc.DownloadFile(url, file);
            }
        }

        /// <summary>
        /// Tâche planifiée : lance Temperator en administrateur sans demande UAC, à l'ouverture de session si demandé.
        /// Toujours créée : le raccourci s'en sert. « Parallel » : relancer depuis le raccourci démarre une 2e instance
        /// qui demande à la 1re de se réafficher, puis s'arrête.
        /// </summary>
        static void RegisterTask(Options o, string user, string exe, string workDir)
        {
            string u = SecurityElement.Escape(user);
            string trigger = o.AutoStart
                ? $"<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{u}</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>"
                : "<Triggers />";
            string xml =
$@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Temperator {Version}</Description></RegistrationInfo>
  {trigger}
  <Principals>
    <Principal id=""Author""><UserId>{u}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>""{SecurityElement.Escape(exe)}""</Command><WorkingDirectory>{SecurityElement.Escape(workDir)}</WorkingDirectory></Exec>
  </Actions>
</Task>";
            string file = Path.Combine(Path.GetTempPath(), "temperator-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try
            {
                int code = Run(Path.Combine(System32, "schtasks.exe"), $"/create /tn \"{o.Name}\" /xml \"{file}\" /f");
                if (code != 0) throw new InvalidOperationException(S.T($"Impossible de créer la tâche de démarrage (code {code}).", $"Couldn't create the startup task (code {code})."));
            }
            finally { TryDelete(file); }
        }

        static bool TaskHasLogonTrigger(string name)
        {
            try { return Capture(Path.Combine(System32, "schtasks.exe"), $"/query /tn \"{name}\" /xml").Contains("<LogonTrigger"); }
            catch { return true; }
        }

        /// <summary>Raccourci : passe par la tâche planifiée (administrateur sans demande UAC), sans fenêtre de console.</summary>
        static void CreateShortcut(string lnk, string dir, string task)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            try
            {
                dynamic s = shell.CreateShortcut(lnk);
                s.TargetPath = Path.Combine(System32, "conhost.exe");
                s.Arguments = $"--headless schtasks.exe /run /tn \"{task}\"";
                s.WorkingDirectory = Path.Combine(dir, "app");
                s.IconLocation = Path.Combine(dir, @"app\Temperator.exe") + ",0";
                s.Description = "Temperator - températures et utilisation du PC";
                s.Save();
                Marshal.FinalReleaseComObject(s);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        static void RegisterUninstall(Options o, string dir, string app)
        {
            string setup = Path.Combine(dir, "Temperator-Setup.exe");
            string un = $"\"{setup}\" /uninstall" + (o.Name != "Temperator" ? $" /name \"{o.Name}\"" : "");
            using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey(o.Name)))
            {
                k.SetValue("DisplayName", "Temperator");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Temperator");
                k.SetValue("DisplayIcon", Path.Combine(app, "Temperator.exe"));
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", un);
                k.SetValue("QuietUninstallString", un + " /S");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                k.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
            }
        }

        static void GrantUsersModify(string dir)
        {
            var di = new DirectoryInfo(dir);
            var acl = di.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(acl);
        }

        // ------------------------------------------------------------------ utilisateur de la session (pas forcément le compte administrateur)

        [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WTSQuerySessionInformation(IntPtr server, uint session, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);
        const int WTSUserName = 5, WTSDomainName = 7;

        static string ConsoleUser()
        {
            try
            {
                uint session = WTSGetActiveConsoleSessionId();
                string user = SessionString(session, WTSUserName), domain = SessionString(session, WTSDomainName);
                if (!string.IsNullOrEmpty(user)) return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
            }
            catch { }
            return Environment.UserDomainName + "\\" + Environment.UserName;
        }

        static string SessionString(uint session, int info)
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, session, info, out var buffer, out _)) return null;
            try { return Marshal.PtrToStringUni(buffer); }
            finally { WTSFreeMemory(buffer); }
        }

        // ------------------------------------------------------------------ outils

        static int Run(string exe, string args, bool hidden = true)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = hidden };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        static string Capture(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return output;
            }
        }

        static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }

        static void DeleteDirectory(string dir)
        {
            for (int i = 0; ; i++)   // un fichier encore ouvert un instant (antivirus…) : on réessaie
            {
                try { Directory.Delete(dir, true); return; }
                catch when (i < 10) { Thread.Sleep(300); }
            }
        }

        static void TryDeleteDirectory(string dir)
        {
            try { if (Directory.Exists(dir)) DeleteDirectory(dir); } catch { }
        }
    }
}
