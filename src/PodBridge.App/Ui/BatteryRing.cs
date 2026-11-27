using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PodBridge.App.Ui;

public enum PartKind { Left, Right, Case, Both }

/// <summary>
/// Circular battery gauge with the part's glyph in the middle, in the spirit of the iOS Batteries
/// widget: thin track, rounded cap, green → orange (≤20 %) → red (≤10 %), animated fill.
/// Glyphs are drawn here (generic earbud and case shapes), not Apple artwork.
/// </summary>
public sealed class BatteryRing : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(int?), typeof(BatteryRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPercentChanged));

    public static readonly DependencyProperty ChargingProperty = DependencyProperty.Register(
        nameof(Charging), typeof(bool), typeof(BatteryRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(PartKind), typeof(BatteryRing),
        new FrameworkPropertyMetadata(PartKind.Left, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SweepProperty = DependencyProperty.Register(
        "Sweep", typeof(double), typeof(BatteryRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty BrushProp(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(BatteryRing),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty TrackBrushProperty = BrushProp("TrackBrush");
    private static readonly DependencyProperty GlyphBrushProperty = BrushProp("GlyphBrush");
    private static readonly DependencyProperty GoodBrushProperty = BrushProp("GoodBrush");
    private static readonly DependencyProperty WarnBrushProperty = BrushProp("WarnBrush");
    private static readonly DependencyProperty LowBrushProperty = BrushProp("LowBrush");

    public BatteryRing()
    {
        Width = Height = 64;
        SetResourceReference(TrackBrushProperty, "TrackBrush");
        SetResourceReference(GlyphBrushProperty, "GlyphBrush");
        SetResourceReference(GoodBrushProperty, "GoodBrush");
        SetResourceReference(WarnBrushProperty, "WarnBrush");
        SetResourceReference(LowBrushProperty, "LowBrush");
    }

    /// <summary>Exposes the ring to screen readers as an image with a spoken name.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new RingPeer(this);

    private sealed class RingPeer(BatteryRing owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(BatteryRing);
        protected override bool IsContentElementCore() => true;
    }

    public int? Percent { get => (int?)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public bool Charging { get => (bool)GetValue(ChargingProperty); set => SetValue(ChargingProperty, value); }
    public PartKind Kind { get => (PartKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    private static void OnPercentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (BatteryRing)d;
        double target = (ring.Percent ?? 0) / 100.0;
        ring.BeginAnimation(SweepProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        const double stroke = 4.5;
        double d = Math.Min(ActualWidth, ActualHeight);
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = (d - stroke) / 2;

        var track = new Pen((Brush)GetValue(TrackBrushProperty), stroke);
        dc.DrawEllipse(null, track, c, r, r);

        double sweep = (double)GetValue(SweepProperty);
        if (Percent is int p && sweep > 0.001)
        {
            var brush = p <= 10 ? (Brush)GetValue(LowBrushProperty)
                      : p <= 20 ? (Brush)GetValue(WarnBrushProperty)
                      : (Brush)GetValue(GoodBrushProperty);
            var pen = new Pen(brush, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Arc(c, r, Math.Min(sweep, 0.9999)));
        }

        var glyph = (Brush)GetValue(GlyphBrushProperty);
        if (Percent is null) glyph = glyph.CloneCurrentValue().Also(b => b.Opacity = 0.35);
        double g = d * 0.42;
        dc.PushTransform(new TranslateTransform(c.X - g / 2, c.Y - g / 2));
        DrawGlyph(dc, glyph, g);
        dc.Pop();
    }

    private static Geometry Arc(Point c, double r, double fraction)
    {
        double a = fraction * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }

    private void DrawGlyph(DrawingContext dc, Brush brush, double s)
    {
        switch (Kind)
        {
            case PartKind.Case: Glyphs.CaseOutline(dc, brush, s); break;
            case PartKind.Both:
                dc.PushTransform(new TranslateTransform(-s * 0.18, 0));
                Glyphs.Bud(dc, brush, null, s * 0.9, mirrored: false);
                dc.Pop();
                dc.PushTransform(new TranslateTransform(s * 0.28, 0));
                Glyphs.Bud(dc, brush, null, s * 0.9, mirrored: true);
                dc.Pop();
                break;
            default: Glyphs.Bud(dc, brush, null, s, mirrored: Kind == PartKind.Right); break;
        }
    }
}

internal static class FreezableExtensions
{
    public static T Also<T>(this T obj, Action<T> act) { act(obj); return obj; }
}
