using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Temperator;

/// <summary>Petite flèche qui ouvre (vers le bas) ou ferme le panneau des ventilateurs.</summary>
sealed class Chevron
{
    public readonly Border Root;
    readonly RotateTransform turn = new();

    public Chevron(Palette palette, Action click)
    {
        var path = new Path
        {
            Data = Geometry.Parse("M 0,0 L 4,4 L 8,0"), Stroke = palette.Muted, StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = turn,
        };
        Root = new Border
        {
            Width = 18, Height = 14, Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Child = path, ToolTip = L.T("Ventilateurs", "Fans"),
        };
        Root.MouseEnter += (_, _) => path.Stroke = palette.Text;
        Root.MouseLeave += (_, _) => path.Stroke = palette.Muted;
        // géré ici : pas de déplacement du widget ni de double-clic (plan du PC)
        Root.MouseLeftButtonDown += (_, e) => { e.Handled = true; click(); };
    }

    bool? open;

    /// <summary>Ouvert : la flèche pointe vers le haut (animée, sauf au premier affichage).</summary>
    public void Set(bool open)
    {
        if (this.open == open) return;
        bool first = this.open == null;
        this.open = open;
        if (first) { turn.BeginAnimation(RotateTransform.AngleProperty, null); turn.Angle = open ? 180 : 0; return; }
        turn.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(open ? 180 : 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}

/// <summary>Curseur de 0 à 100 %, aux couleurs du widget (glisser ou molette).</summary>
sealed class FanSlider : FrameworkElement
{
    const double Knob = 5;
    readonly Palette palette;
    double value;
    bool dim;

    /// <summary>Valeur changée par l'utilisateur ; <c>true</c> quand il a relâché (ou à la molette).</summary>
    public event Action<double, bool>? Changed;
    public double Minimum { get; init; }
    public bool Dragging { get; private set; }

    public FanSlider(Palette palette)
    {
        this.palette = palette;
        Height = 16;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public double Value
    {
        get => value;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (Math.Abs(value - this.value) < 0.01) return;
            this.value = value;
            InvalidateVisual();
        }
    }

    /// <summary>Automatique : le curseur suit la vitesse en cours, en gris.</summary>
    public bool Dim
    {
        get => dim;
        set { if (dim == value) return; dim = value; InvalidateVisual(); }
    }

    protected override Size MeasureOverride(Size available) => new(40, 16);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, cy = ActualHeight / 2;
        if (w <= 2 * Knob) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));   // toute la hauteur est saisissable
        double x0 = Knob, x1 = w - Knob, x = x0 + (x1 - x0) * value / 100;
        Brush fill = dim ? palette.Muted : palette.Accent;
        dc.DrawRoundedRectangle(palette.Track, null, new Rect(x0, cy - 2, x1 - x0, 4), 2, 2);
        if (x > x0) dc.DrawRoundedRectangle(fill, null, new Rect(x0, cy - 2, x - x0, 4), 2, 2);
        dc.DrawEllipse(fill, null, new Point(x, cy), Knob, Knob);
    }

    void Drag(MouseEventArgs e, bool final)
    {
        double w = ActualWidth - 2 * Knob;
        if (w <= 0) return;
        double v = Math.Round(Math.Clamp((e.GetPosition(this).X - Knob) / w * 100, Minimum, 100));
        bool changed = Math.Abs(v - value) >= 0.5;
        Value = v;
        if (changed || final) Changed?.Invoke(v, final);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;   // pas de déplacement du widget
        Dragging = true;
        CaptureMouse();
        Drag(e, false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Dragging) Drag(e, false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!Dragging) return;
        e.Handled = true;
        Dragging = false;
        ReleaseMouseCapture();
        Changed?.Invoke(value, true);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (!Dragging) return;
        Dragging = false;
        Changed?.Invoke(value, true);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        double v = Math.Clamp(Math.Round(value / 5) * 5 + (e.Delta > 0 ? 5 : -5), Minimum, 100);
        Value = v;
        Changed?.Invoke(v, true);
    }
}

/// <summary>
/// Panneau sous les jauges, les lignes ou la barre : chaque ventilateur à part, sa vitesse, et un curseur
/// pour la régler (« AUTO » : le BIOS ou le pilote décide).
/// </summary>
sealed class FanPanel
{
    public readonly StackPanel Root = new() { Margin = new Thickness(0, 8, 0, 0), MinWidth = 150 };
    readonly Palette palette;
    readonly StackPanel list = new();
    readonly TextBlock note, boost;
    readonly Dictionary<string, FanLine> lines = new();
    string signature = "";

    /// <summary>Un réglage change : identifiant, vitesse en % (null : automatique), et s'il est définitif (souris relâchée).</summary>
    public event Action<string, float?, bool>? Changed;

    public FanPanel(Palette palette)
    {
        this.palette = palette;
        Root.Children.Add(new Border { Height = 1, Background = palette.Line, Margin = new Thickness(0, 0, 0, 6) });
        Root.Children.Add(new TextBlock
        {
            Text = L.T("VENTILATEURS", "FANS"), FontFamily = Theme.TitleFont, FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = palette.Muted, Margin = new Thickness(0, 0, 0, 3),
        });
        boost = new TextBlock
        {
            Text = L.T("Trop chaud : ventilateurs à fond", "Too hot: fans at full speed"), FontSize = 10.5, Foreground = palette.Hot,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3), Visibility = Visibility.Collapsed,
        };
        note = new TextBlock { FontSize = 10.5, Foreground = palette.Soft, TextWrapping = TextWrapping.Wrap, MaxWidth = 218, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        Root.Children.Add(boost);
        Root.Children.Add(list);
        Root.Children.Add(note);
    }

    public bool Visible
    {
        set => Root.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <param name="wanted">vitesses imposées (réglages), par identifiant</param>
    /// <param name="message">texte affiché sans ventilateur (droits administrateur manquants…)</param>
    public void Update(IReadOnlyList<FanInfo> fans, IReadOnlyDictionary<string, double> wanted, bool boosting, string? message)
    {
        string sig = string.Join("|", fans.Select(f => f.Key + (f.ControlId != null ? "+" : "")));
        if (sig != signature)
        {
            signature = sig;
            list.Children.Clear();
            var old = new Dictionary<string, FanLine>(lines);
            lines.Clear();
            foreach (var f in fans)
            {
                if (lines.ContainsKey(f.Key)) continue;
                if (!old.TryGetValue(f.Key, out var line)) line = new FanLine(this, f.Key, f.ControlId != null);
                lines[f.Key] = line;
                list.Children.Add(line.Root);
            }
        }
        foreach (var f in fans) lines[f.Key].Update(f, wanted);

        boost.Visibility = boosting ? Visibility.Visible : Visibility.Collapsed;
        string? text = fans.Count == 0
            ? message ?? L.T("Aucun ventilateur détecté : la carte mère ne les montre pas à Windows.", "No fan found: the motherboard doesn't expose them to Windows.")
            : fans.All(f => f.ControlId == null)
                ? L.T("Ces ventilateurs ne se règlent pas depuis Windows (seulement depuis le BIOS).", "These fans can't be set from Windows (only from the BIOS).")
                : null;
        note.Text = text ?? "";
        note.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Nom lisible et court : « Fan #2 » → « VENTILO 2 », « GPU Fan 1 » → « VENTILO GPU 1 ».</summary>
    internal static string Pretty(string name)
    {
        string n = name.Trim();
        n = n.Replace("Fan Control #", "Fan #", StringComparison.OrdinalIgnoreCase);
        if (L.Fr)
        {
            n = n.Replace("GPU Fan", "Ventilo GPU", StringComparison.OrdinalIgnoreCase)
                 .Replace("CPU Fan", "Ventilo CPU", StringComparison.OrdinalIgnoreCase)
                 .Replace("Fan #", "Ventilo ", StringComparison.OrdinalIgnoreCase)
                 .Replace("Fan", "Ventilo", StringComparison.OrdinalIgnoreCase)
                 .Replace("Pump", "Pompe", StringComparison.OrdinalIgnoreCase);
        }
        else n = n.Replace("Fan #", "Fan ", StringComparison.OrdinalIgnoreCase);
        return n.ToUpper(L.Culture);
    }

    /// <summary>Un ventilateur : NOM · vitesse, puis AUTO · curseur · %.</summary>
    sealed class FanLine
    {
        public readonly StackPanel Root = new() { Margin = new Thickness(0, 2, 0, 3), Background = Brushes.Transparent };
        readonly FanPanel panel;
        readonly string id;
        readonly TextBlock name;
        readonly Run rpm = new("—"), unit = new();
        readonly Border auto;
        readonly TextBlock autoText, pct;
        readonly FanSlider? slider;
        bool manual;
        DateTime holdUntil;

        public FanLine(FanPanel panel, string id, bool settable)
        {
            this.panel = panel;
            this.id = id;
            var p = panel.palette;

            var top = new DockPanel();
            rpm.FontSize = 13;
            rpm.FontWeight = FontWeights.SemiBold;
            rpm.Foreground = p.Text;
            unit.FontSize = 10;
            unit.Foreground = p.Muted;
            var speed = new TextBlock { FontFamily = Theme.TitleFont, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            speed.Inlines.Add(rpm);
            speed.Inlines.Add(unit);
            DockPanel.SetDock(speed, Dock.Right);
            name = new TextBlock
            {
                FontFamily = Theme.TitleFont, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = p.Muted,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            top.Children.Add(speed);
            top.Children.Add(name);
            Root.Children.Add(top);

            autoText = new TextBlock { Text = "AUTO", FontFamily = Theme.TitleFont, FontSize = 9.5, FontWeight = FontWeights.SemiBold };
            auto = new Border
            {
                CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Padding = new Thickness(5, 0, 5, 1),
                VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = autoText,
                ToolTip = L.T("Automatique : le BIOS ou le pilote règle la vitesse", "Automatic: the BIOS or the driver sets the speed"),
            };
            pct = new TextBlock { FontSize = 11, Foreground = p.Soft, Width = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            if (!settable)
            {
                Root.ToolTip = L.T("Vitesse lue seulement : ce ventilateur ne se règle pas depuis Windows.", "Read-only: this fan can't be set from Windows.");
                return;
            }

            slider = new FanSlider(p) { Minimum = AppSettings.MinFanSpeed, Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            slider.Changed += (v, final) =>
            {
                holdUntil = DateTime.UtcNow.AddSeconds(3);   // le temps que le matériel suive
                SetManual(true, v);
                panel.Changed?.Invoke(id, (float)v, final);
            };
            auto.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                holdUntil = DateTime.UtcNow.AddSeconds(3);
                if (manual)
                {
                    SetManual(false, slider.Value);
                    panel.Changed?.Invoke(id, null, true);
                }
                else
                {
                    double v = Math.Max(slider.Value, AppSettings.MinFanSpeed);
                    SetManual(true, v);
                    panel.Changed?.Invoke(id, (float)v, true);
                }
            };

            var bottom = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            DockPanel.SetDock(auto, Dock.Left);
            DockPanel.SetDock(pct, Dock.Right);
            bottom.Children.Add(auto);
            bottom.Children.Add(pct);
            bottom.Children.Add(slider);
            Root.Children.Add(bottom);
            SetManual(false, 0);
        }

        void SetManual(bool on, double value)
        {
            manual = on;
            var p = panel.palette;
            auto.BorderBrush = on ? p.Line : p.Accent;
            autoText.Foreground = on ? p.Muted : p.Accent;
            if (slider != null)
            {
                slider.Dim = !on;
                slider.Value = value;
            }
            pct.Text = $"{value:0} %";
        }

        public void Update(FanInfo f, IReadOnlyDictionary<string, double> wanted)
        {
            name.Text = Pretty(f.Name);
            name.ToolTip = f.Device;
            string r = f.Rpm is float v ? v.ToString("N0", Fmt.Fr) : "—";
            if (rpm.Text != r) rpm.Text = r;
            unit.Text = f.Rpm.HasValue ? " " + Fmt.Rpm : "";

            if (slider == null || slider.Dragging || DateTime.UtcNow < holdUntil) return;
            bool imposed = wanted.TryGetValue(f.ControlId!, out double target);
            if (imposed) SetManual(true, target);
            else SetManual(f.Manual, f.Duty ?? 0);   // un autre logiciel peut l'avoir réglé : on le montre tel quel
        }
    }
}
