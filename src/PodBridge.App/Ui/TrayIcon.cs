using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PodBridge.App.Ui;

/// <summary>
/// Monochrome tray glyph (like macOS menu-bar icons): white on a dark taskbar, black on a light
/// one, dimmed when disconnected, with a small orange dot when a pod is low.
///
/// Talks to the Windows notification area directly (Shell_NotifyIcon), so PodBridge doesn't
/// need WinForms just for this. Left click / Enter toggles the panel, double click opens Settings,
/// right click / Shift+F10 shows a native menu (dark when Windows is dark).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const int MenuSettings = 1, MenuQuit = 2;

    private readonly HwndSource _window;
    private readonly int _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private IntPtr _hIcon;
    private string _tooltip = "PodBridge";
    private bool _added;
    private DateTime _lastKeySelect, _lastDoubleClick;
    private System.Windows.Threading.DispatcherTimer? _retry;

    public event Action? Clicked;
    public event Action? QuitRequested;
    public event Action? SettingsRequested;

    public TrayIcon()
    {
        // A hidden top-level window (not message-only: those don't get the TaskbarCreated broadcast).
        _window = new HwndSource(new HwndSourceParameters("PodBridge tray")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = 0x80,               // WS_EX_TOOLWINDOW: not in Alt+Tab
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);
        AllowDarkMenus();
        Update(connected: false, lowBattery: false, tooltip: "PodBridge");
    }

    public void Update(bool connected, bool lowBattery, string tooltip)
    {
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        var old = _hIcon;
        _hIcon = CreateGlyph(connected, lowBattery);
        Publish(_added ? NIM_MODIFY : NIM_ADD);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private void Publish(int message)
    {
        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _hIcon;
        data.szTip = _tooltip;
        bool ok = Shell_NotifyIcon(message, ref data);
        if (message == NIM_ADD)
        {
            if (ok)
            {
                data.uVersion = NOTIFYICON_VERSION_4; // keyboard selection + context-menu messages
                Shell_NotifyIcon(NIM_SETVERSION, ref data);
                _added = true;
                _retry?.Stop();
            }
            else RetryAddLater(); // Explorer's tray isn't ready yet (e.g. right after sign-in)
        }
        else if (!ok)
        {
            _added = false; // the icon is gone (Explorer restarted?): add it again
            Publish(NIM_ADD);
        }
    }

    private void RetryAddLater()
    {
        if (_retry is not null) { _retry.Start(); return; }
        int attempts = 0;
        _retry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _retry.Tick += (_, _) =>
        {
            if (_added || ++attempts > 60) { _retry!.Stop(); return; }
            Publish(NIM_ADD);
        };
        _retry.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _added = false; // Explorer restarted: the old icon is gone
            Publish(NIM_ADD);
            return IntPtr.Zero;
        }
        if (msg != CallbackMessage) return IntPtr.Zero;

        int ev = (int)(lParam.ToInt64() & 0xFFFF);
        switch (ev)
        {
            case NIN_SELECT:
                // The second click of a double click is not a toggle (WinForms behaved the same).
                if ((DateTime.UtcNow - _lastDoubleClick).TotalMilliseconds > 500) Clicked?.Invoke();
                break;
            case NIN_KEYSELECT:
                // Enter sends this twice; one toggle per key press.
                if ((DateTime.UtcNow - _lastKeySelect).TotalMilliseconds > 400) Clicked?.Invoke();
                _lastKeySelect = DateTime.UtcNow;
                break;
            case WM_LBUTTONDBLCLK:
                _lastDoubleClick = DateTime.UtcNow;
                SettingsRequested?.Invoke();
                break;
            case WM_CONTEXTMENU:
                long w = wParam.ToInt64();
                ShowMenu((short)(w & 0xFFFF), (short)((w >> 16) & 0xFFFF));
                break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, MenuSettings, "AirPods Settings…");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, MenuQuit, "Quit");
            // Documented tray-menu dance: foreground first so the menu closes when you click away.
            SetForegroundWindow(_window.Handle);
            int chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_NONOTIFY,
                x, y, _window.Handle, IntPtr.Zero);
            PostMessage(_window.Handle, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL
            if (chosen == MenuSettings) SettingsRequested?.Invoke();
            else if (chosen == MenuQuit) QuitRequested?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    /// <summary>Lets native menus follow Windows' dark mode (uxtheme, Windows 10 1903+).</summary>
    private static void AllowDarkMenus()
    {
        try { SetPreferredAppMode(1); FlushMenuThemes(); } // 1 = AllowDark: follow the system
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }

    private static IntPtr CreateGlyph(bool connected, bool low)
    {
        const int s = 32;
        bool lightTaskbar = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is int v && v == 1;
        byte alpha = connected ? (byte)255 : (byte)130;
        var brush = new SolidColorBrush(lightTaskbar ? Color.FromArgb(alpha, 20, 20, 20) : Color.FromArgb(alpha, 255, 255, 255));

        var visual = new DrawingVisual();
        using (var g = visual.RenderOpen())
        {
            // Two earbuds, mirrored: a round head and a stem with rounded ends.
            g.DrawEllipse(brush, null, new Point(7.5, 9.5), 5.5, 5.5);
            g.DrawRoundedRectangle(brush, null, new Rect(8, 9, 5, 19.5), 2.5, 2.5);
            g.DrawEllipse(brush, null, new Point(24.5, 9.5), 5.5, 5.5);
            g.DrawRoundedRectangle(brush, null, new Rect(19, 9, 5, 19.5), 2.5, 2.5);
            if (low) g.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 149, 0)), null, new Point(26, 26), 5, 5);
        }
        var bmp = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        png.Save(ms);
        var bytes = ms.ToArray();
        return CreateIconFromResourceEx(bytes, bytes.Length, true, 0x00030000, s, s, 0);
    }

    private NOTIFYICONDATAW NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = IconId,
        szTip = "", szInfo = "", szInfoTitle = "",
    };

    public void Dispose()
    {
        _retry?.Stop();
        if (_added)
        {
            var data = NewData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        _window.RemoveHook(WndProc);
        _window.Dispose();
        if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
    }

    // ---------------- Win32 ----------------

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    private const int NOTIFYICON_VERSION_4 = 4;
    private const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, WM_CONTEXTMENU = 0x7B, WM_LBUTTONDBLCLK = 0x203;
    private const int MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    private const int TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion; // union with uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, int flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, int size, bool icon, int version, int cx, int cy, int flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
}
