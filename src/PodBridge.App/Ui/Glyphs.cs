using System.Windows;
using System.Windows.Media;

namespace PodBridge.App.Ui;

/// <summary>Generic earbud / case shapes drawn in code (not Apple artwork).</summary>
internal static class Glyphs
{
    public static void Bud(DrawingContext dc, Brush fill, Pen? outline, double s, bool mirrored)
    {
        if (mirrored) dc.PushTransform(new ScaleTransform(-1, 1, s / 2, 0));
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(s * 0.40, s * 0.30), s * 0.28, s * 0.28));
        g.Children.Add(new RectangleGeometry(new Rect(s * 0.50, s * 0.30, s * 0.22, s * 0.70), s * 0.11, s * 0.11));
        dc.DrawGeometry(fill, outline, outline is null ? g : g.GetOutlinedPathGeometry());
        if (mirrored) dc.Pop();
    }

    public static void CaseOutline(DrawingContext dc, Brush stroke, double s)
    {
        var body = new Rect(s * 0.04, s * 0.16, s * 0.92, s * 0.7);
        dc.DrawRoundedRectangle(null, new Pen(stroke, s * 0.08), body, s * 0.22, s * 0.22);
        dc.DrawLine(new Pen(stroke, s * 0.06), new Point(body.Left + s * 0.04, s * 0.38), new Point(body.Right - s * 0.04, s * 0.38));
    }
}

/// <summary>Header illustration for Settings: an open case with both buds, soft gradient, drawn in code.</summary>
public sealed class DeviceArt : FrameworkElement
{
    public DeviceArt() { Width = 120; Height = 120; }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        var white = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xE3, 0xE3, 0xE8), 90);
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x8E, 0x8E, 0x93)), 1);
        var shade = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0));

        // soft floor shadow
        dc.DrawEllipse(shade, null, new Point(s * 0.5, s * 0.94), s * 0.36, s * 0.04);

        // case body
        var body = new Rect(s * 0.12, s * 0.48, s * 0.76, s * 0.44);
        dc.DrawRoundedRectangle(white, edge, body, s * 0.16, s * 0.16);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x8E, 0x8E, 0x93)), 1),
            new Point(body.Left + s * 0.02, body.Top + s * 0.12), new Point(body.Right - s * 0.02, body.Top + s * 0.12));
        // status light
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)), null,
            new Point(s * 0.5, body.Top + s * 0.24), s * 0.012, s * 0.012);

        // buds rising out of the case
        double b = s * 0.42;
        dc.PushTransform(new TranslateTransform(s * 0.10, s * 0.10));
        dc.PushTransform(new RotateTransform(-12, b / 2, b / 2));
        Glyphs.Bud(dc, white, edge, b, mirrored: false);
        dc.Pop(); dc.Pop();
        dc.PushTransform(new TranslateTransform(s * 0.48, s * 0.10));
        dc.PushTransform(new RotateTransform(12, b / 2, b / 2));
        Glyphs.Bud(dc, white, edge, b, mirrored: true);
        dc.Pop(); dc.Pop();
    }
}
