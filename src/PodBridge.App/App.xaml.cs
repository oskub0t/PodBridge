using System.Windows;
using PodBridge.App.Platform;
using PodBridge.App.Ui;
using Forms = System.Windows.Forms;

namespace PodBridge.App;

public partial class App : Application
{
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write($"Unhandled: {ex.Exception}");
            ex.Handled = true;
        };

        // Helper modes run once and exit; they never become the tray app.
        int apply = Array.IndexOf(args, "--apply-update");
        if (apply >= 0 && apply + 1 < args.Length)
        {
            Updater.ApplyUpdate(args[apply + 1]);
            Shutdown();
            return;
        }
        if (args.Contains("--uninstall"))
        {
            RunUninstall();
            Shutdown();
            return;
        }

        // One PodBridge per user. After install/update the new copy waits for the old one to exit.
        var wait = args.Contains("--wait-for-previous") ? TimeSpan.FromSeconds(20) : TimeSpan.Zero;
        if (!SingleInstance.TryAcquire(wait)) { Shutdown(); return; }
        SingleInstance.ListenForQuit(() => Dispatcher.BeginInvoke(() => Shutdown()));

        Notifications.Init();
        Updater.CleanUp();
        ThemeManager.Apply();
        _controller = new AppController();
        ThemeManager.Follow(_controller.RefreshUi);

        bool background = args.Contains("--background") || Notifications.WasLaunchedByNotification();
        _controller.Start(showPanel: !background, justUpdated: args.Contains("--updated"));
    }

    /// <summary>Settings → Apps → Uninstall (or Settings → Uninstall in PodBridge).</summary>
    private static void RunUninstall()
    {
        bool removeSettings;
        try
        {
            Forms.Application.EnableVisualStyles();
            var keep = new Forms.TaskDialogVerificationCheckBox("Also remove my PodBridge settings");
            var uninstall = new Forms.TaskDialogButton("Uninstall");
            var page = new Forms.TaskDialogPage
            {
                Caption = "PodBridge",
                Heading = "Uninstall PodBridge?",
                Text = "This removes PodBridge from Start and Settings → Apps, turns off Open at login, and deletes the app. Your AirPods stay paired with Windows.",
                Buttons = { uninstall, Forms.TaskDialogButton.Cancel },
                Verification = keep,
                Icon = Forms.TaskDialogIcon.None,
            };
            if (Forms.TaskDialog.ShowDialog(page) != uninstall) return;
            removeSettings = keep.Checked;
        }
        catch (Exception ex)
        {
            Log.Write($"Task dialog unavailable ({ex.Message}); using a message box");
            var answer = MessageBox.Show("Uninstall PodBridge? Your settings are kept.", "PodBridge",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
            removeSettings = false;
        }

        SingleInstance.WaitForOtherToExit(TimeSpan.FromSeconds(10)); // close the running tray app first
        Installer.Uninstall(removeSettings);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        SingleInstance.Release();
        base.OnExit(e);
    }
}
