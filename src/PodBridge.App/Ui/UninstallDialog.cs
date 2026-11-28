using System.Windows;
using System.Windows.Controls;

namespace PodBridge.App.Ui;

/// <summary>
/// "Uninstall PodBridge?" with an "also remove my settings" check box. A small WPF window in the
/// Windows 11 theme, like Settings (it used to be a WinForms TaskDialog).
/// </summary>
internal sealed class UninstallDialog : Window
{
    private readonly CheckBox _removeSettings = new()
    {
        Content = "Also remove my PodBridge settings",
        Margin = new Thickness(0, 16, 0, 0),
    };

    /// <summary>Null if cancelled; otherwise whether to remove the settings too.</summary>
    public static bool? Ask()
    {
        var d = new UninstallDialog();
        return d.ShowDialog() == true ? d._removeSettings.IsChecked == true : null;
    }

    private UninstallDialog()
    {
#pragma warning disable WPF0001 // Fluent theme is still flagged experimental in .NET 10
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        Title = "PodBridge";
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/PodBridge;component/Assets/PodBridge.ico"));
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        if (TryFindResource("TextFont") is System.Windows.Media.FontFamily font) FontFamily = font;

        var heading = new TextBlock { Text = "Uninstall PodBridge?", FontSize = 20, FontWeight = FontWeights.SemiBold };
        var text = new TextBlock
        {
            Text = "This removes PodBridge from Start and Settings → Apps, turns off Open at login, and deletes the app. Your AirPods stay paired with Windows.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var uninstall = new Button { Content = "Uninstall", IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        if (TryFindResource("AccentButtonStyle") is Style accent) uninstall.Style = accent;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 100 };
        uninstall.Click += (_, _) => DialogResult = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0),
            Children = { uninstall, cancel },
        };
        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20),
            Children = { heading, text, _removeSettings, buttons },
        };
    }
}
