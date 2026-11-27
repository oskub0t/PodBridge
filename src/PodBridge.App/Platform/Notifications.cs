using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace PodBridge.App.Platform;

/// <summary>
/// Standard Windows notifications: they stay in Notification Center, follow Focus / Do Not Disturb,
/// and can be turned off per app in Windows Settings. Works for unpackaged apps without admin
/// (the toolkit registers PodBridge for the current user only). If notifications are disabled
/// (by the user or company policy), <see cref="Show"/> returns false and callers fall back to
/// PodBridge's own pop-up.
/// </summary>
internal static class Notifications
{
    /// <summary>A notification or one of its buttons was clicked: (action, arguments). UI thread.</summary>
    public static event Action<string, ToastArguments>? Activated;

    public static void Init()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                var action = args.TryGetValue("action", out string? a) ? a : "open";
                Application.Current?.Dispatcher.BeginInvoke(() => Activated?.Invoke(action, args));
            };
        }
        catch (Exception ex) { Log.Write($"Notifications unavailable: {ex.Message}"); }
    }

    public static bool WasLaunchedByNotification()
    {
        try { return ToastNotificationManagerCompat.WasCurrentProcessToastActivated(); }
        catch { return false; }
    }

    public static bool Enabled
    {
        get
        {
            try { return ToastNotificationManagerCompat.CreateToastNotifier().Setting == NotificationSetting.Enabled; }
            catch { return false; }
        }
    }

    /// <param name="tag">Same tag replaces the previous notification of that kind.</param>
    /// <param name="buttons">(label, action) pairs; the action comes back through <see cref="Activated"/>.</param>
    /// <returns>False if Windows notifications are off or failed (show a pop-up instead).</returns>
    public static bool Show(string tag, string title, string body, params (string Label, string Action)[] buttons)
    {
        if (!Enabled) return false;
        try
        {
            var b = new ToastContentBuilder()
                .AddArgument("action", "open")
                .AddText(title)
                .AddText(body);
            foreach (var (label, action) in buttons)
                b.AddButton(new ToastButton().SetContent(label).AddArgument("action", action));
            b.Show(t => { t.Tag = tag; t.Group = "podbridge"; });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Notification failed: {ex.Message}");
            return false;
        }
    }

    public static void Remove(string tag)
    {
        try { ToastNotificationManagerCompat.History.Remove(tag, "podbridge"); } catch { }
    }

    /// <summary>Removes PodBridge's notification registration (used by uninstall).</summary>
    public static void Unregister()
    {
        try { ToastNotificationManagerCompat.Uninstall(); } catch { }
    }
}
