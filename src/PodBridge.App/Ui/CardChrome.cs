using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace PodBridge.App.Ui;

/// <summary>
/// On Windows 11 22H2+ cards use the real system Acrylic material (DWM), rounded corners and
/// shadow, the same as the Quick Settings and Bluetooth flyouts. That's the Windows equivalent of
/// Apple's translucent materials. Elsewhere, or if turned off in settings, they fall back to the
/// drawn translucent card.
/// Call <see cref="Prepare"/> in the window constructor, before it is shown.
///
/// Windows only draws Acrylic on the ACTIVE window; inactive ones get a flat fallback colour. The
/// tray panel is activated when you click the icon, but the case card deliberately isn't (it must
/// not steal the keyboard from what you're typing), so it looked solid. For such windows
/// (<c>activeLook</c>) we tell the frame it's active (WM_NCACTIVATE TRUE, the same trick tool palettes
/// use) without giving it focus, and keep it that way when it loses focus.
/// </summary>
internal static class CardChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    public static bool UseMaterials { get; set; } = true;

    public static bool MaterialsAvailable =>
        UseMaterials && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    public static void Prepare(Window w, Border card, bool activeLook = false)
    {
        if (!MaterialsAvailable) return;
        if (activeLook) KeepActiveLook(w);

        w.AllowsTransparency = false;
        w.WindowStyle = WindowStyle.SingleBorderWindow; // keeps the DWM frame (shadow, rounding)
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
            NonClientFrameEdges = NonClientFrameEdges.None,
        });

        card.Margin = new Thickness(0);
        card.CornerRadius = new CornerRadius(0);
        card.BorderThickness = new Thickness(0);
        card.Effect = null;
        card.SetResourceReference(Border.BackgroundProperty, "CardTintBrush");

        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
                target.BackgroundColor = Colors.Transparent;
            int dark = ThemeManager.IsDark ? 1 : 0, round = DWMWCP_ROUND, backdrop = DWMSBT_TRANSIENTWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            // The extended frame lets Windows paint its own caption buttons over the card (a stray
            // second close button). Cards aren't app windows: drop the system menu and buttons.
            const int GWL_STYLE = -16;
            const long WS_SYSMENU = 0x80000, WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000;
            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~(WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)));
        };
    }

    private const int WM_NCACTIVATE = 0x0086;

    private static void KeepActiveLook(Window w)
    {
        // Added on Loaded, after WindowChrome's own hook, so this one runs first.
        w.Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (HwndSource.FromHwnd(hwnd) is not { } source) return;
            source.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg != WM_NCACTIVATE || wParam != IntPtr.Zero) return IntPtr.Zero;
                handled = true; // losing focus: let it go, but keep the active frame (and Acrylic)
                return DefWindowProc(h, WM_NCACTIVATE, new IntPtr(1), new IntPtr(-1));
            });
            SendMessage(hwnd, WM_NCACTIVATE, new IntPtr(1), IntPtr.Zero);
        };
    }

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
