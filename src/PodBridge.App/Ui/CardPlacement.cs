using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PodBridge.App.Ui;

/// <summary>
/// Puts a card next to the system tray (bottom-right of the work area, or wherever the taskbar is)
/// and gives it the same entrance/exit every time: short rise + fade with ease-out. Motion is brief
/// and directional so it reads as "came from the tray", never decorative.
/// </summary>
internal static class CardPlacement
{
    private const double Rise = 16;

    /// <summary>
    /// Places the card by the tray and keeps its bottom edge anchored there when its height
    /// changes (content updates after it's shown), the way Windows' own flyouts behave.
    /// </summary>
    public static void AnchorNearTray(Window w, bool usesSystemChrome = false)
    {
        PlaceNearTray(w, usesSystemChrome);
        w.SizeChanged += (_, _) => PlaceNearTray(w, usesSystemChrome);
    }

    public static void PlaceNearTray(Window w, bool usesSystemChrome = false)
    {
        // Windows 11 flyouts sit 12 px off the screen edge; drawn cards carry their own margin.
        double gap = usesSystemChrome ? 12 : 0;
        var area = SystemParameters.WorkArea;
        var screenW = SystemParameters.PrimaryScreenWidth;
        w.UpdateLayout();
        double width = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
        double height = w.ActualHeight > 0 ? w.ActualHeight : 300;

        // The window rectangle can be larger than what's visible (invisible frame borders);
        // place by the visible edges so the gap really is 12 px on every side.
        var hidden = InvisibleFrame(w);

        bool taskbarTop = area.Top > 0;
        bool taskbarLeft = area.Left > 0 && area.Width < screenW;

        w.Left = taskbarLeft ? area.Left + gap - hidden.Left : area.Right - width - gap + hidden.Right;
        w.Top = taskbarTop ? area.Top + gap - hidden.Top : area.Bottom - height - gap + hidden.Bottom;
    }

    /// <summary>How far the window rectangle extends beyond its visible frame, in WPF units.</summary>
    private static Thickness InvisibleFrame(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var wr) ||
            DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var fr, Marshal.SizeOf<Rect>()) != 0)
            return default;
        var m = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Thickness(
            Math.Max(0, fr.Left - wr.Left) * m.M11, Math.Max(0, fr.Top - wr.Top) * m.M22,
            Math.Max(0, wr.Right - fr.Right) * m.M11, Math.Max(0, wr.Bottom - fr.Bottom) * m.M22);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private const int DwmwaExtendedFrameBounds = 9;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

    public static void AnimateIn(Window w)
    {
        if (w.Content is not FrameworkElement root) return;
        var tt = new TranslateTransform(0, Rise);
        root.RenderTransform = tt;
        root.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(280);
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
        root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
    }

    public static void AnimateOutAndClose(Window w, Action? after = null)
    {
        if (w.Content is not FrameworkElement root) { w.Close(); return; }
        var tt = root.RenderTransform as TranslateTransform ?? new TranslateTransform();
        root.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var dur = TimeSpan.FromMilliseconds(180);
        var fade = new DoubleAnimation(0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) => { w.Close(); after?.Invoke(); };
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Rise / 2, dur) { EasingFunction = ease });
        root.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
