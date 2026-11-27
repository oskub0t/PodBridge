using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace PodBridge.App.Ui;

/// <summary>
/// Monochrome tray glyph (like macOS menu-bar icons): white on a dark taskbar, black on a light
/// one, dimmed when disconnected, with a small orange dot when a pod is low.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon = new() { Visible = true, Text = "PodBridge" };
    private IntPtr _hIcon;

    public event Action? Clicked;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open PodBridge", null, (_, _) => Clicked?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke(); };
        Update(connected: false, lowBattery: false, tooltip: "PodBridge");
    }

    public void Update(bool connected, bool lowBattery, string tooltip)
    {
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        var old = _hIcon;
        using (var bmp = Draw(connected, lowBattery))
        {
            _hIcon = bmp.GetHicon();
            _icon.Icon = Icon.FromHandle(_hIcon);
        }
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private static Bitmap Draw(bool connected, bool low)
    {
        const int s = 32;
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool lightTaskbar = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is int v && v == 1;
        int alpha = connected ? 255 : 130;
        var color = lightTaskbar ? Color.FromArgb(alpha, 20, 20, 20) : Color.FromArgb(alpha, 255, 255, 255);
        using var b = new SolidBrush(color);

        // Two earbuds, mirrored.
        void Bud(float ox, bool mirror)
        {
            var state = g.Save();
            if (mirror) { g.TranslateTransform(ox * 2 + 12, 0); g.ScaleTransform(-1, 1); }
            g.FillEllipse(b, ox, 4, 11, 11);
            using var path = new GraphicsPath();
            path.AddArc(ox + 6, 9, 5, 5, 180, 180);
            path.AddLine(ox + 11, 11.5f, ox + 11, 26);
            path.AddArc(ox + 6, 23.5f, 5, 5, 0, 180);
            path.CloseFigure();
            g.FillPath(b, path);
            g.Restore(state);
        }
        Bud(2, false);
        Bud(18, true);

        if (low)
        {
            using var warn = new SolidBrush(Color.FromArgb(255, 255, 149, 0));
            g.FillEllipse(warn, 21, 21, 10, 10);
        }
        return bmp;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
