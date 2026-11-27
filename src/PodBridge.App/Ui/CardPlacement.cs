using System.Windows;
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

    public static void PlaceNearTray(Window w)
    {
        var area = SystemParameters.WorkArea;
        var screenW = SystemParameters.PrimaryScreenWidth;
        w.UpdateLayout();
        double width = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
        double height = w.ActualHeight > 0 ? w.ActualHeight : 300;

        bool taskbarTop = area.Top > 0;
        bool taskbarLeft = area.Left > 0 && area.Width < screenW;

        w.Left = taskbarLeft ? area.Left : area.Right - width;
        w.Top = taskbarTop ? area.Top : area.Bottom - height;
    }

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
