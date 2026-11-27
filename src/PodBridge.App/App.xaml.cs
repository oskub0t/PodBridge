using System.Windows;
using PodBridge.App.Platform;
using PodBridge.App.Ui;

namespace PodBridge.App;

public partial class App : Application
{
    private Mutex? _single;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\PodBridge.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write($"Unhandled: {ex.Exception}");
            ex.Handled = true;
        };

        ThemeManager.Apply();
        _controller = new AppController();
        ThemeManager.Follow(_controller.RefreshUi);
        _controller.Start(showPanel: !e.Args.Contains("--background"));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
