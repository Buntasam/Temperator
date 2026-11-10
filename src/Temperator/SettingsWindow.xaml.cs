using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Temperator;

/// <summary>Réglages (mode complet) : toutes les options, rangées par onglets. Chaque changement s'applique tout de suite.</summary>
public partial class SettingsWindow : Window
{
    readonly App app;
    readonly Dictionary<string, RadioButton> nav = new();
    string current = "";

    public static readonly (string Key, string Icon, string Fr, string En)[] Pages =
    {
        ("look", "", "Apparence", "Appearance"),
        ("info", "", "Informations", "Information"),
        ("alerts", "", "Températures", "Temperatures"),
        ("behavior", "", "Comportement", "Behavior"),
        ("games", "", "Jeux et plein écran", "Games & fullscreen"),
        ("updates", "", "Mises à jour", "Updates"),
        ("general", "", "Général", "General"),
    };

    static readonly (string Hex, string Fr, string En)[] Accents =
    {
        ("#4ED8C0", "Turquoise", "Teal"), ("#5AA9FF", "Bleu", "Blue"), ("#A78BFA", "Violet", "Purple"),
        ("#F472B6", "Rose", "Pink"), ("#4ADE80", "Vert", "Green"), ("#FB923C", "Orange", "Orange"), ("#E5E7EB", "Blanc", "White"),
    };

    public SettingsWindow(App app, string page = "look")
    {
        this.app = app;
        InitializeComponent();
        Title = L.T("Réglages – Temperator", "Settings – Temperator");
        Heading.Text = L.T("Réglages", "Settings");
        SubHeading.Text = L.T("Les changements s'appliquent tout de suite.", "Changes apply right away.");

        foreach (var (key, icon, fr, en) in Pages)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Width = 26, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = L.T(fr, en), VerticalAlignment = VerticalAlignment.Center });
            var item = new RadioButton { Style = (Style)FindResource("NavItem"), GroupName = "nav", Content = content };
            item.Checked += (_, _) => { if (current != key) ShowPage(key); };
            nav[key] = item;
            Nav.Children.Add(item);
        }

        TitleBar.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (InvalidOperationException) { } };
        // MouseDown (pas MouseUp) : sinon le déplacement de la fenêtre (DragMove sur la barre de titre) avale le clic
        CloseX.MouseLeftButtonDown += (_, e) => { e.Handled = true; Close(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => app.Settings.Save();   // les curseurs ne sont enregistrés qu'à la fermeture
        ShowPage(page);
    }

    AppSettings S => app.Settings;

    public void ShowPage(string key)
    {
        current = key;
        if (nav.TryGetValue(key, out var item)) item.IsChecked = true;
        Page.Children.Clear();
        switch (key)
        {
            case "info": InfoPage(); break;
            case "alerts": AlertsPage(); break;
            case "behavior": BehaviorPage(); break;
            case "games": GamesPage(); break;
            case "updates": UpdatesPage(); break;
            case "general": GeneralPage(); break;
            default: LookPage(); break;
        }
        Scroller.ScrollToTop();
    }

    /// <summary>Un réglage a changé : on l'applique et on l'enregistre.</summary>
    void Changed() => app.Changed();

    // ------------------------------------------------------------------ pages

    void LookPage()
    {
        Section(L.T("AFFICHAGE", "LAYOUT"));
        Row(L.T("Disposition", "Layout"), L.T("La barre fine tient sur une seule ligne, très discrète.", "The thin bar fits on a single line, very discreet."),
            Pills(S.Layout, v => S.Layout = v, (L.T("Jauges rondes", "Round gauges"), AppSettings.Gauges), (L.T("Lignes et barres", "Rows and bars"), AppSettings.Rows), (L.T("Barre fine", "Thin bar"), AppSettings.Bar)));
        Row(L.T("Taille", "Size"), null, Pills(S.Scale, v => S.Scale = v,
            (L.T("Petite", "Small"), 0.85), (L.T("Normale", "Normal"), 1.0), (L.T("Grande", "Large"), 1.2), (L.T("Très grande", "Extra large"), 1.45)));

        Section(L.T("FOND", "BACKGROUND"));
        Row(L.T("Fond", "Background"), L.T("Translucide et sans fond adaptent les couleurs à ce qu'il y a derrière.", "Translucent and none adapt colors to what's behind."),
            Pills(S.Transparent ? 2 : S.Translucent ? 1 : 0, v => { S.Translucent = v == 1; S.Transparent = v == 2; },
                (L.T("Opaque", "Opaque"), 0), (L.T("Translucide", "Translucent"), 1), (L.T("Aucun fond", "None"), 2)), redraw: true);
        var theme = Pills(S.Theme, v => S.Theme = v,
            (L.T("Sombre", "Dark"), AppSettings.Dark), (L.T("Clair", "Light"), AppSettings.Light), (L.T("Comme Windows", "Like Windows"), AppSettings.FollowWindows));
        theme.IsEnabled = !S.Translucent && !S.Transparent;
        Row(L.T("Thème du fond opaque", "Opaque background theme"), L.T("« Comme Windows » suit le mode clair ou sombre de Windows.", "\"Like Windows\" follows the Windows light or dark mode."), theme);
        var bg = Slider(S.BackgroundOpacity, 0, 1, v => $"{v * 100:0} %", v => S.BackgroundOpacity = v);
        bg.IsEnabled = !S.Transparent;
        Row(L.T("Opacité du fond", "Background opacity"), L.T("Le fond seul : le texte reste net.", "The background only: text stays sharp."), bg);
        Row(L.T("Opacité générale", "Overall opacity"), null, Slider(S.Opacity, 0.3, 1, v => $"{v * 100:0} %", v => S.Opacity = v));

        Section(L.T("COULEUR", "COLOR"));
        Row(L.T("Couleur d'accent", "Accent color"), L.T("Jauges, barres et logo.", "Gauges, bars and logo."), Swatches());

        Section(L.T("ANIMATIONS", "ANIMATIONS"));
        Row(L.T("Flammes quand c'est trop chaud", "Flames when too hot"), L.T("Sur la jauge concernée, ou en bas du widget en lignes.", "On the gauge concerned, or along the bottom in rows."),
            Switch(S.FireAnimation, v => S.FireAnimation = v));
    }

    void InfoPage()
    {
        Section(L.T("À AFFICHER, ET DANS QUEL ORDRE", "SHOWN, AND IN WHICH ORDER"));
        Note(L.T("Les flèches déplacent un élément dans le widget.", "The arrows move an item in the widget."));
        var order = S.Order;
        for (int i = 0; i < order.Count; i++)
        {
            string key = order[i];
            int index = i;
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(Arrow("\uE70E", L.T("Monter", "Move up"), index > 0, () => Move(index, -1)));
            controls.Children.Add(Arrow("\uE70D", L.T("Descendre", "Move down"), index < order.Count - 1, () => Move(index, +1)));
            var sw = Switch(S.Shows(key), v => S.SetShows(key, v));
            sw.Margin = new Thickness(10, 0, 0, 0);
            controls.Children.Add(sw);
            Row(MetricKeys.Name(key), Description(key), controls);
        }

        Section(L.T("MATÉRIEL", "HARDWARE"));
        var r = app.Last;
        Row(L.T("Carte graphique", "Graphics card"), L.T("Automatique : la carte dédiée en priorité.", "Automatic: the dedicated card first."),
            Hardware(r.Gpus, S.GpuId, v => S.GpuId = v), stacked: true);
        Row(L.T("Disque", "Drive"), L.T("Automatique : le plus chaud.", "Automatic: the hottest one."),
            Hardware(r.Disks, S.DiskId, v => S.DiskId = v), stacked: true);
        Row(L.T("Carte réseau", "Network adapter"), L.T("Automatique : la plus active.", "Automatic: the busiest one."),
            Hardware(r.Nets, S.NetId, v => S.NetId = v), stacked: true);

        Section(L.T("MESURES", "READINGS"));
        Row(L.T("Actualisation", "Refresh"), null, Pills(S.IntervalMs, v => S.IntervalMs = v,
            ("0,5 s", 500), ("1 s", 1000), ("2 s", 2000), ("5 s", 5000)));
        Row(L.T("Unité de température", "Temperature unit"), null, Pills(S.Fahrenheit, v => S.Fahrenheit = v, ("°C", false), ("°F", true)), redraw: true);
    }

    static string? Description(string key) => key switch
    {
        MetricKeys.Disk => L.T("Température et activité.", "Temperature and activity."),
        MetricKeys.Fan => L.T("Le plus rapide.", "The fastest one."),
        MetricKeys.Net => L.T("Réception ↓ et envoi ↑.", "Download ↓ and upload ↑."),
        MetricKeys.Battery => L.T("Portables : niveau, en charge ou watts consommés.", "Laptops: level, charging or watts drawn."),
        MetricKeys.Power => L.T("CPU + GPU, en watts (estimation).", "CPU + GPU, in watts (estimate)."),
        MetricKeys.Fps => L.T("Pendant un jeu en plein écran (DirectX).", "During a fullscreen game (DirectX)."),
        _ => null,
    };

    /// <summary>Déplace un élément d'un cran dans l'ordre du widget.</summary>
    void Move(int index, int delta)
    {
        var order = S.Order;
        int target = index + delta;
        if (target < 0 || target >= order.Count) return;
        (order[index], order[target]) = (order[target], order[index]);
        Changed();
        double offset = Scroller.VerticalOffset;
        ShowPage(current);
        Scroller.ScrollToVerticalOffset(offset);
    }

    static Button Arrow(string glyph, string tip, bool enabled, Action action)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("ActionButton"), ToolTip = tip, IsEnabled = enabled, Margin = new Thickness(4, 0, 0, 0),
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11 },
            Opacity = enabled ? 1 : 0.3,
        };
        b.Click += (_, _) => action();
        return b;
    }

    void AlertsPage()
    {
        Section(L.T("SEUILS « TROP CHAUD »", "\"TOO HOT\" THRESHOLDS"));
        Note(L.T("Au-delà : valeur en rouge, flammes et notification. L'orange commence un peu avant. " +
                 "Certains processeurs de portables tournent normalement vers 90 °C.",
                 "Above it: red value, flames and notification. Orange starts a little earlier. " +
                 "Some laptop processors normally run around 90 °C."));
        string Deg(double v) => S.Fahrenheit ? $"{Fmt.Temp((float)v, true):0} °F" : $"{v:0} °C";
        Row("CPU", null, Slider(S.CpuHot, 60, 105, Deg, v => S.CpuHot = Math.Round(v), step: 1));
        Row("GPU", null, Slider(S.GpuHot, 60, 100, Deg, v => S.GpuHot = Math.Round(v), step: 1));
        Row(L.T("Disque", "Drive"), null, Slider(S.DiskHot, 40, 80, Deg, v => S.DiskHot = Math.Round(v), step: 1));

        Section(L.T("NOTIFICATIONS", "NOTIFICATIONS"));
        Row(L.T("Prévenir quand c'est trop chaud", "Notify when too hot"),
            L.T("Après 30 secondes au-dessus du seuil, même widget masqué. Au plus une fois toutes les 10 minutes.",
                "After 30 seconds above the threshold, even with the widget hidden. At most once every 10 minutes."),
            Switch(S.HeatAlerts, v => S.HeatAlerts = v));
    }

    void BehaviorPage()
    {
        Section(L.T("PREMIER PLAN", "ON TOP"));
        Row(L.T("Position par rapport aux fenêtres", "Position over windows"),
            L.T("« Au-dessus de tout » repasse devant toute fenêtre qui le recouvre.", "\"Above everything\" comes back over any window covering it."),
            Pills(!S.Topmost ? 0 : S.TopmostForce ? 2 : 1, v => { S.Topmost = v > 0; S.TopmostForce = v == 2; },
                (L.T("Normal", "Normal"), 0), (L.T("Toujours au premier plan", "Always on top"), 1), (L.T("Au-dessus de tout", "Above everything"), 2)), stacked: true);

        Section(L.T("SOURIS ET CLAVIER", "MOUSE AND KEYBOARD"));
        Row(L.T("Clics traversants", "Click-through"), L.T("La souris passe à travers. Maintiens Ctrl pour le saisir.", "The mouse goes through it. Hold Ctrl to grab it."),
            Switch(S.ClickThrough, v => S.ClickThrough = v));
        Row(L.T("Verrouiller la position", "Lock position"), null, Switch(S.Locked, v => S.Locked = v));
        Row(L.T("Raccourci afficher / masquer", "Show / hide shortcut"), null, Pills(S.Hotkey, v => S.Hotkey = v,
            (L.T("Aucun", "None"), ""), ("Ctrl+Alt+T", "Ctrl+Alt+T"), (L.T("Ctrl+Maj+T", "Ctrl+Shift+T"), "Ctrl+Shift+T"), ("Ctrl+Alt+H", "Ctrl+Alt+H")));

        Section(L.T("POSITION", "POSITION"));
        Row(L.T("Remettre le widget à sa place", "Put the widget back"), L.T("En haut à droite de l'écran principal.", "Top right of the main screen."),
            Button(L.T("Replacer", "Reset position"), () => app.ResetWidgetPosition()));
    }

    void GamesPage()
    {
        Section(L.T("JEU OU VIDÉO EN PLEIN ÉCRAN", "GAME OR VIDEO IN FULLSCREEN"));
        Note(L.T("Détecté automatiquement sur l'écran du widget. Les jeux en plein écran « exclusif » ne peuvent être recouverts par aucune application : " +
                 "choisis le mode « fenêtré sans bordure » dans le jeu.",
                 "Detected automatically on the widget's screen. Games in \"exclusive\" fullscreen can't be overlaid by any app: " +
                 "choose \"borderless windowed\" in the game."));
        Row(L.T("Pendant le plein écran", "During fullscreen"), null, Pills(S.GameMode, v => S.GameMode = v,
            (L.T("Profil « Jeu » (réglé ci-dessous)", "\"Game\" profile (set below)"), AppSettings.GameProfile),
            (L.T("Masquer le widget", "Hide the widget"), AppSettings.GameHide),
            (L.T("Ne rien changer", "Change nothing"), AppSettings.GameOff)), stacked: true, redraw: true);
        if (S.GameMode != AppSettings.GameProfile) return;

        Section(L.T("PROFIL « JEU »", "\"GAME\" PROFILE"));
        Note(L.T("S'applique tout seul pendant le plein écran, puis le widget revient à son profil « Bureau » (les autres onglets).",
                 "Applies by itself during fullscreen, then the widget goes back to its \"Desktop\" profile (the other tabs)."));
        var chips = new WrapPanel { MaxWidth = 470 };
        foreach (var key in MetricKeys.All)
        {
            var chip = new CheckBox { Style = (Style)FindResource("PillToggle"), Content = MetricKeys.Name(key), IsChecked = S.GameShow.Contains(key) };
            chip.Click += (_, _) =>
            {
                S.GameShow.Remove(key);
                if (chip.IsChecked == true) S.GameShow.Add(key);
                Changed();
            };
            chips.Children.Add(chip);
        }
        Row(L.T("À afficher pendant un jeu", "Shown during a game"), L.T("Les FPS : jeux DirectX 9 à 12 (pas les jeux Vulkan ou OpenGL purs).", "FPS: DirectX 9 to 12 games (not pure Vulkan or OpenGL games)."), chips, stacked: true);
        Row(L.T("Disposition", "Layout"), null, Pills(S.GameLayout, v => S.GameLayout = v,
            (L.T("Comme au bureau", "Same as desktop"), (string?)null), (L.T("Jauges rondes", "Round gauges"), AppSettings.Gauges),
            (L.T("Lignes et barres", "Rows and bars"), AppSettings.Rows), (L.T("Barre fine", "Thin bar"), AppSettings.Bar)), stacked: true);
        Row(L.T("Taille", "Size"), null, Slider(S.GameScale, 0.6, 1.5, v => $"{v * 100:0} %", v => S.GameScale = v));
        Row(L.T("Opacité du fond", "Background opacity"), null, Slider(S.GameBackgroundOpacity, 0, 1, v => $"{v * 100:0} %", v => S.GameBackgroundOpacity = v));
    }

    void UpdatesPage()
    {
        Section(L.T("MISES À JOUR", "UPDATES"));
        Row(L.T("Nouvelles versions", "New versions"),
            L.T("Vérifiées une fois par jour sur GitHub. L'empreinte SHA-256 est contrôlée avant l'installation.",
                "Checked once a day on GitHub. The SHA-256 fingerprint is verified before installing."),
            Pills(S.UpdateMode, v => S.UpdateMode = v,
                (L.T("Automatiques", "Automatic"), Updater.Auto), (L.T("Me prévenir seulement", "Notify me only"), Updater.NotifyOnly), (L.T("Désactivées", "Off"), Updater.Off)), stacked: true);
        Row(L.T("Version installée", "Installed version") + " : " + App.Version, null,
            Button(L.T("Rechercher maintenant", "Check now"), () => app.CheckForUpdates()));
    }

    void GeneralPage()
    {
        Section(L.T("LANGUE", "LANGUAGE"));
        Row(L.T("Langue de l'interface", "Interface language"), L.T("Temperator redémarre pour l'appliquer.", "Temperator restarts to apply it."),
            PillsCore(S.Language, v => { S.Language = v; app.Settings.Save(); app.Restart(); }, false,
                new[] { (L.T("Celle de Windows", "Same as Windows"), L.Auto), ("Français", L.French), ("English", L.English) }));

        Section(L.T("MODE", "MODE"));
        Row(L.T("Simple ou complet", "Simple or full"), L.T("Le mode simple garde un menu court, sans la fenêtre de réglages.", "Simple mode keeps a short menu, without the settings window."),
            PillsCore(S.IsLite, v => { app.SetMode(v ? AppSettings.Lite : AppSettings.Full); if (v) Close(); }, false,
                new[] { (L.T("Simple", "Simple"), true), (L.T("Complet", "Full"), false) }));

        Section(L.T("OUTILS", "TOOLS"));
        Row(L.T("Exporter l'historique", "Export history"), L.T("Les 24 dernières heures (moyennes de 30 s), au format CSV pour Excel.", "The last 24 hours (30 s averages), as CSV for Excel."),
            Button(L.T("Exporter…", "Export…"), () => app.ExportHistory(this)));
        Row(L.T("Rapport de diagnostic", "Diagnostic report"), L.T("Copié dans le presse-papiers, à coller dans une issue GitHub.", "Copied to the clipboard, to paste into a GitHub issue."),
            Button(L.T("Copier", "Copy"), () => app.CopyDiagnostics()));
        Row(L.T("Licence et conditions d'utilisation", "License and terms of use"), null, Button(L.T("Lire", "Read"), App.OpenLicense));
        Row("GitHub", L.T("Signaler un problème, proposer une idée.", "Report a problem, suggest an idea."),
            Button(L.T("Ouvrir", "Open"), () => App.OpenUrl($"https://github.com/{Updater.Repo}")));
    }

    // ------------------------------------------------------------------ briques

    void Section(string title) =>
        Page.Children.Add(new TextBlock
        {
            Text = title, FontFamily = Theme.TitleFont, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Theme.Accent,
            Margin = new Thickness(0, Page.Children.Count == 0 ? 0 : 18, 0, 10),
        });

    void Note(string text) =>
        Page.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 12) });

    /// <summary>Une ligne : titre (et explication) à gauche, réglage à droite — ou dessous si <paramref name="stacked"/>.</summary>
    void Row(string title, string? sub, FrameworkElement control, bool stacked = false, bool redraw = false)
    {
        // la page change selon ce choix (éléments grisés…) : on la redessine après le clic
        if (redraw && control is Panel choices)
            foreach (var rb in choices.Children.OfType<RadioButton>())
                rb.Click += (_, _) => Dispatcher.InvokeAsync(() => ShowPage(current));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap });
        if (sub != null) text.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });

        if (stacked)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(text);
            control.Margin = new Thickness(0, 8, 0, 0);
            control.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(control);
            Page.Children.Add(panel);
        }
        else
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14), LastChildFill = true };
            DockPanel.SetDock(control, Dock.Right);
            control.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(control);
            row.Children.Add(text);
            Page.Children.Add(row);
        }
    }

    CheckBox Switch(bool value, Action<bool> set)
    {
        var box = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = value };
        box.Click += (_, _) => { set(box.IsChecked == true); Changed(); };
        return box;
    }

    /// <summary>Un choix parmi plusieurs, en pastilles.</summary>
    WrapPanel Pills<T>(T current, Action<T> set, params (string Label, T Value)[] options) => PillsCore(current, set, true, options);

    WrapPanel PillsCore<T>(T current, Action<T> set, bool apply, (string Label, T Value)[] options)
    {
        var panel = new WrapPanel { MaxWidth = 470 };
        string group = Guid.NewGuid().ToString("N");
        foreach (var (label, value) in options)
        {
            var pill = new RadioButton { Style = (Style)FindResource("Pill"), GroupName = group, Content = label, IsChecked = Equals(value, current) };
            pill.Click += (_, _) => { set(value); if (apply) Changed(); };
            panel.Children.Add(pill);
        }
        return panel;
    }

    /// <summary>Le matériel au choix : « Automatique » puis chaque élément détecté.</summary>
    FrameworkElement Hardware(List<(string Id, string Name)> items, string? current, Action<string?> set)
    {
        var options = new List<(string, string?)> { (L.T("Automatique", "Automatic"), null) };
        options.AddRange(items.Select(i => (i.Name, (string?)i.Id)));
        if (items.Count == 0)
        {
            var panel = new StackPanel();
            panel.Children.Add(PillsCore(current, set, true, options.ToArray()));
            panel.Children.Add(new TextBlock { Text = L.T("Rien de détecté pour l'instant.", "Nothing detected yet."), FontSize = 12, Foreground = Theme.Muted });
            return panel;
        }
        return PillsCore(current, set, true, options.ToArray());
    }

    FrameworkElement Slider(double value, double min, double max, Func<double, string> format, Action<double> set, double step = 0.05)
    {
        var label = new TextBlock { Width = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Soft, FontSize = 12.5, Text = format(value) };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value, Width = 200, IsMoveToPointEnabled = true,
            SmallChange = step, LargeChange = step * 5, VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            label.Text = format(e.NewValue);
            set(e.NewValue);
            app.ApplyLive();
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slider);
        row.Children.Add(label);
        return row;
    }

    FrameworkElement Swatches()
    {
        var panel = new WrapPanel { MaxWidth = 330 };
        foreach (var (hex, fr, en) in Accents)
        {
            var brush = new SolidColorBrush(Palette.Parse(hex));
            brush.Freeze();
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"), GroupName = "accent", Background = brush, ToolTip = L.T(fr, en),
                IsChecked = string.Equals(S.Accent, hex, StringComparison.OrdinalIgnoreCase),
            };
            swatch.Click += (_, _) => { S.Accent = hex; Changed(); };
            panel.Children.Add(swatch);
        }
        return panel;
    }

    static Button Button(string text, Action action)
    {
        var b = new Button { Style = (Style)Application.Current.FindResource("ActionButton"), Content = text };
        b.Click += (_, _) => action();
        return b;
    }
}
