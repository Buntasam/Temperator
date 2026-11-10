using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace TemperatorSetup
{
    public partial class SetupWindow : Window
    {
        const string Ring = "M 12.17,45.83 A 23.8,23.8 0 1 1 45.83,45.83";
        const string Spinner = "M 12.17,45.83 A 23.8,23.8 0 0 1 9.5,15.35";   // arc court qui tourne
        const string CheckMark = "M 19,30 L 26,37 L 40,22";
        const string CrossMark = "M 22,22 L 36,36 M 36,22 L 22,36";
        const string Tagline = "Températures et utilisation de ton PC, toujours à l'écran.";

        static readonly Color Accent = Color.FromRgb(0x4E, 0xD8, 0xC0);
        static readonly Color Danger = Color.FromRgb(0xFF, 0x5A, 0x5F);
        static readonly Color Muted = Color.FromRgb(0x8E, 0x97, 0xA4);

        readonly Options opt;
        readonly bool uninstallMode;
        readonly bool needRuntime, needPawn;
        bool busy;
        bool confirmUninstall;   // le bouton principal lance la désinstallation

        internal SetupWindow(Options o, bool uninstall, bool preview = false)
        {
            InitializeComponent();
            opt = o;
            uninstallMode = uninstall;
            Ver.Text = "v" + Engine.Version;
            PathText.Text = "Dossier : " + Engine.Dir(o.Name);

            needRuntime = !preview && !Engine.HasRuntime();
            needPawn = !preview && !Engine.HasPawnIO();
            ApplyMissing(needRuntime, needPawn);

            if (!preview && Engine.IsInstalled(o.Name))
            {
                BtnInstall.Content = "Mettre à jour";
                BtnGoUninstall.Visibility = Visibility.Visible;
            }

            // la licence doit être acceptée avant d'installer
            LicenseText.Text = ReadLicense();
            BtnInstall.IsEnabled = false;
            OptLicense.Checked += (_, _) => BtnInstall.IsEnabled = true;
            OptLicense.Unchecked += (_, _) => BtnInstall.IsEnabled = false;
            LicenseLink.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowPage(PageLicense); };   // le lien a son propre clic
            RowLicense.MouseLeftButtonUp += (_, _) => OptLicense.IsChecked = OptLicense.IsChecked != true;
            BtnBack.Click += (_, _) => ShowPage(PageOptions);
            BtnAccept.Click += (_, _) => { OptLicense.IsChecked = true; ShowPage(PageOptions); };

            // un clic n'importe où sur la ligne bascule l'interrupteur
            RowAuto.MouseLeftButtonUp += (_, _) => OptAuto.IsChecked = OptAuto.IsChecked != true;
            RowDesk.MouseLeftButtonUp += (_, _) => OptDesk.IsChecked = OptDesk.IsChecked != true;
            RowLaunch.MouseLeftButtonUp += (_, _) => OptLaunch.IsChecked = OptLaunch.IsChecked != true;

            Card.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (InvalidOperationException) { } };
            CloseX.MouseLeftButtonUp += (_, _) => { if (!busy) Close(); };
            Closing += (_, e) => { if (busy) e.Cancel = true; };
            BtnCancel.Click += (_, _) => Close();
            BtnGoUninstall.Click += (_, _) => ShowUninstallConfirm();
            BtnSecond.Click += (_, _) =>
            {
                if (uninstallMode) { Close(); return; }
                Subtitle.Text = Tagline;
                SetLogoColor(Accent);
                SetMark(null);
                confirmUninstall = false;
                ShowPage(PageOptions);
            };
            BtnInstall.Click += (_, _) => StartWork(false);
            BtnMain.Click += (_, _) => { if (confirmUninstall) StartWork(true); else Close(); };
            ContentRendered += (_, _) => Activate();   // au premier plan, même lancé depuis le navigateur

            if (uninstall) ShowUninstallConfirm();
        }

        static string ReadLicense()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LICENSE.txt"))
            {
                if (s == null) return "Licence : https://github.com/Buntasam/Temperator/blob/main/LICENSE";
                using (var r = new StreamReader(s)) return Reflow(r.ReadToEnd());
            }
        }

        /// <summary>Le fichier est coupé à ~90 caractères : on recolle les paragraphes pour l'affichage.</summary>
        static string Reflow(string text)
        {
            var sb = new System.Text.StringBuilder();
            bool blank = true;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    if (!blank) sb.Append("\n\n");
                    blank = true;
                    continue;
                }
                bool item = System.Text.RegularExpressions.Regex.IsMatch(line, @"^[a-z]\.\s");
                bool start = blank || item || line.StartsWith("---") || line.StartsWith("Copyright");
                if (!start) sb.Append(' ');
                else if (!blank) sb.Append('\n');
                if (item) sb.Append("    ");
                sb.Append(line);
                blank = false;
            }
            return sb.ToString().TrimEnd();
        }

        void ApplyMissing(bool runtime, bool pawn)
        {
            int n = (runtime ? 1 : 0) + (pawn ? 1 : 0);
            Missing.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            AllGood.Visibility = n > 0 ? Visibility.Collapsed : Visibility.Visible;
            RowDotNet.Visibility = runtime ? Visibility.Visible : Visibility.Collapsed;
            RowPawn.Visibility = pawn ? Visibility.Visible : Visibility.Collapsed;
            Height = 530 + (n > 0 ? 30 + 60 * n : 0);
        }

        // ------------------------------------------------------------------ écrans

        void SetLogoColor(Color c)
        {
            LogoArc.Stroke = new SolidColorBrush(c);
            LogoGlow.Color = c;
        }

        /// <summary>Coche, croix, ou le point du logo.</summary>
        void SetMark(string geometry)
        {
            if (geometry != null)
            {
                LogoMark.Data = Geometry.Parse(geometry);
                LogoMark.Visibility = Visibility.Visible;
                LogoDot.Visibility = Visibility.Collapsed;
            }
            else
            {
                LogoMark.Visibility = Visibility.Collapsed;
                LogoDot.Visibility = Visibility.Visible;
            }
        }

        void ShowPage(FrameworkElement page)
        {
            foreach (var p in new FrameworkElement[] { PageOptions, PageLicense, PageProgress, PageMessage })
                p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }

        void ShowMessage(string subtitle, string text, Color color, string mark, string button, bool withCancel = false)
        {
            LogoRot.BeginAnimation(RotateTransform.AngleProperty, null);
            LogoRot.Angle = 0;
            LogoArc.Data = Geometry.Parse(Ring);
            SetLogoColor(color);
            SetMark(mark);
            Subtitle.Text = subtitle;
            Message.Text = text;
            BtnMain.Content = button;
            BtnMain.Background = new SolidColorBrush(color);
            BtnSecond.Visibility = withCancel ? Visibility.Visible : Visibility.Collapsed;
            ShowPage(PageMessage);
        }

        void ShowUninstallConfirm()
        {
            ShowMessage("Désinstaller Temperator de ce PC ?",
                "Le widget, son démarrage automatique, ses raccourcis et ses réglages seront supprimés. " +
                "Le pilote PawnIO et .NET 10 restent installés (d'autres logiciels peuvent s'en servir).",
                Danger, null, "Désinstaller", withCancel: true);
            confirmUninstall = true;
        }

        static string DoneText(bool launched) =>
            (launched ? "Il est lancé : regarde en haut à droite de l'écran. " : "Lance-le depuis le menu Démarrer. ") +
            "Glisse-le pour le déplacer, double-clic pour le plan du PC, clic droit pour les réglages. " +
            "Son icône près de l'horloge le masque ou le réaffiche, Ctrl+Alt+T aussi.";

        // ------------------------------------------------------------------ travail en arrière-plan : la fenêtre reste fluide

        async void StartWork(bool uninstall)
        {
            busy = true;
            confirmUninstall = false;
            opt.AutoStart = OptAuto.IsChecked == true;
            opt.Desktop = OptDesk.IsChecked == true;
            opt.Launch = OptLaunch.IsChecked == true;

            var color = uninstall ? Danger : Accent;
            SetLogoColor(color);
            SetMark(null);
            Fill.Background = new SolidColorBrush(color);
            LogoArc.Data = Geometry.Parse(Spinner);
            LogoRot.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            Subtitle.Text = uninstall ? "Désinstallation en cours…" : "Installation en cours…";
            Hint.Visibility = uninstall || !needRuntime ? Visibility.Collapsed : Visibility.Visible;
            Status.Text = "Préparation";
            FillScale.ScaleX = 0;
            ShowPage(PageProgress);

            var progress = new Progress<Step>(s =>
            {
                Status.Text = s.Text;
                FillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(s.Value, TimeSpan.FromMilliseconds(250)));
            });

            string error = null;
            try
            {
                await Task.Run(() =>
                {
                    if (uninstall) Engine.Uninstall(opt, progress);
                    else Engine.Install(opt, progress);
                });
            }
            catch (Exception e) { error = e.Message; }

            busy = false;
            if (error != null) ShowMessage("Un problème est survenu.", error, Danger, CrossMark, "Fermer");
            else if (uninstall) ShowMessage("Désinstallé.", "Temperator a été retiré de ce PC.", Muted, CheckMark, "Terminer");
            else ShowMessage("C'est installé !", DoneText(opt.Launch), Accent, CheckMark, "Terminer");
        }

        // ------------------------------------------------------------------ aperçu (développement) : chaque écran en PNG

        internal void RenderPreview(string dir)
        {
            Directory.CreateDirectory(dir);
            void Snap(string file)
            {
                Card.Measure(new Size(Width, Height));
                Card.Arrange(new Rect(0, 0, Width, Height));
                Card.UpdateLayout();
                foreach (var p in new UIElement[] { PageOptions, PageLicense, PageProgress, PageMessage })
                {
                    p.BeginAnimation(OpacityProperty, null);
                    p.Opacity = 1;
                }
                var rtb = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(Card);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(Path.Combine(dir, file))) enc.Save(fs);
            }

            Snap("1-options.png");
            OptLicense.IsChecked = true;
            Snap("1a-options-licence-acceptee.png");
            ShowPage(PageLicense);
            Snap("1c-licence.png");
            ShowPage(PageOptions);
            ApplyMissing(true, true);
            Snap("1b-options-pc-sans-dotnet-ni-pawnio.png");
            ApplyMissing(false, false);
            Subtitle.Text = "Installation en cours…";
            LogoArc.Data = Geometry.Parse(Spinner);
            Status.Text = "Installation de .NET 10 (Microsoft, environ 57 Mo)";
            FillScale.ScaleX = 0.35;
            ShowPage(PageProgress);
            Snap("2-progression.png");
            ShowMessage("C'est installé !", DoneText(true), Accent, CheckMark, "Terminer");
            Snap("3-termine.png");
            ShowUninstallConfirm();
            Snap("4-desinstaller.png");
            ShowMessage("Un problème est survenu.",
                "Temperator est installé, mais le pilote PawnIO (obligatoire) n'a pas pu s'installer. Vérifie ta connexion internet et relance l'installeur, ou installe PawnIO depuis pawnio.eu.",
                Danger, CrossMark, "Fermer");
            Snap("5-erreur.png");
        }
    }
}
