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

/// <summary>Le menu du clic droit (widget et icône près de l'horloge).</summary>
public partial class App
{
    // ------------------------------------------------------------------ menu (clic droit sur le widget ou l'icône)

    public void ShowMenu(UIElement? target)
    {
        var menu = BuildMenu();
        menu.Opened += (_, _) => widget.MenuOpen(true);
        menu.Closed += (_, _) => widget.MenuOpen(false);
        menu.Placement = PlacementMode.MousePoint;
        if (target != null) menu.PlacementTarget = target;
        else Native.SetForegroundWindow(new WindowInteropHelper(widget).Handle);   // pour que le menu se ferme en cliquant ailleurs
        menu.IsOpen = true;
    }

    ContextMenu BuildMenu()
    {
        var s = Settings;
        var m = new ContextMenu();
        if (updater.Available is string next)
        {
            var up = Item(L.T($"Mettre à jour vers la version {next}", $"Update to version {next}"), () => _ = updater.Install());
            up.FontWeight = FontWeights.SemiBold;
            up.Foreground = Theme.Accent;
            m.Items.Add(up);
        }
        var plan = Item(L.T("Plan du PC…", "PC map…"), ShowMap);
        plan.FontWeight = FontWeights.SemiBold;
        m.Items.Add(plan);
        if (!s.IsLite) m.Items.Add(Item(L.T("Réglages…", "Settings…"), () => ShowSettings()));
        m.Items.Add(new Separator());

        m.Items.Add(Choice(L.T("Affichage", "Layout"), s.Layout, v => s.Layout = v, (L.T("Jauges rondes", "Round gauges"), AppSettings.Gauges), (L.T("Lignes et barres", "Rows and bars"), AppSettings.Rows), (L.T("Barre fine", "Thin bar"), AppSettings.Bar)));
        if (s.IsLite)
        {
            // mode simple : l'essentiel, rien de plus
            m.Items.Add(Choice(L.T("Taille", "Size"), s.Scale, v => s.Scale = v,
                (L.T("Petite", "Small"), 0.85), (L.T("Normale", "Normal"), 1.0), (L.T("Grande", "Large"), 1.2), (L.T("Très grande", "Extra large"), 1.45)));
            m.Items.Add(Toggle(L.T("Toujours au premier plan", "Always on top"), s.Topmost, v => { s.Topmost = v; s.TopmostForce = false; }));
            m.Items.Add(Item(L.T("Replacer en haut à droite", "Move back to top right"), () => widget.ResetPosition()));
            m.Items.Add(new Separator());
            var full = Item(L.T("Mode complet : toutes les options…", "Full mode: every option…"), () => { SetMode(AppSettings.Full); ShowSettings(); });
            full.Foreground = Theme.Accent;
            m.Items.Add(full);
        }
        else
        {
            m.Items.Add(Choice(L.T("Fond", "Background"), s.Transparent ? 2 : s.Translucent ? 1 : 0, v => { s.Translucent = v == 1; s.Transparent = v == 2; },
                (L.T("Opaque", "Opaque"), 0), (L.T("Translucide", "Translucent"), 1), (L.T("Aucun fond", "None"), 2)));
            m.Items.Add(new Separator());
        }
        m.Items.Add(Item(widget.IsVisible ? L.T("Masquer le widget", "Hide the widget") : L.T("Afficher le widget", "Show the widget"), ToggleWidget));
        m.Items.Add(Item(L.T("Quitter Temperator", "Quit Temperator"), Quit));
        return m;
    }

    static MenuItem Item(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        return item;
    }

    MenuItem Toggle(string text, bool value, Action<bool> set)
    {
        var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = value };
        item.Click += (_, _) => { set(item.IsChecked); Changed(); };
        return item;
    }

    MenuItem Choice<T>(string text, T current, Action<T> set, params (string Label, T Value)[] options)
    {
        var sub = new MenuItem { Header = text };
        foreach (var (label, value) in options)
        {
            var item = new MenuItem { Header = label, IsChecked = Equals(value, current) };
            item.Click += (_, _) => { set(value); Changed(); };
            sub.Items.Add(item);
        }
        return sub;
    }
}
