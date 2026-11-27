using System.Windows;
using Microsoft.Win32;

namespace PodBridge.App.Ui;

/// <summary>Follows Windows' light/dark app setting live, the way Apple apps follow Appearance.</summary>
internal static class ThemeManager
{
    public static bool IsDark =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int v && v == 0;

    public static void Apply()
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PodBridge;component/Ui/Theme/{(IsDark ? "Dark" : "Light")}.xaml"),
        };
        dicts[0] = palette; // index 0 is the palette, see App.xaml
    }

    public static void Follow(Action? onChanged = null)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            Application.Current.Dispatcher.BeginInvoke(() => { Apply(); onChanged?.Invoke(); });
        };
    }
}
