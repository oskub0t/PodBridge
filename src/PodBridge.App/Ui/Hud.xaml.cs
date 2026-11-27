using System.Windows;
using System.Windows.Threading;

namespace PodBridge.App.Ui;

public enum HudKind { Info, Warning }

/// <summary>Transient, non-interactive notice near the tray. Never takes focus.</summary>
public partial class Hud : Window
{
    private static Hud? _current;

    private Hud() => InitializeComponent();

    public static void Show(string title, string? detail = null, HudKind kind = HudKind.Info, double seconds = 2.6)
    {
        _current?.Close();
        var hud = new Hud();
        _current = hud;
        hud.TitleText.Text = title;
        hud.DetailText.Text = detail ?? "";
        hud.DetailText.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        hud.Dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            kind == HudKind.Warning ? "WarnBrush" : "GoodBrush");
        hud.Loaded += (_, _) =>
        {
            CardPlacement.PlaceNearTray(hud);
            CardPlacement.AnimateIn(hud);
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CardPlacement.AnimateOutAndClose(hud, () => { if (_current == hud) _current = null; });
        };
        ((Window)hud).Show();
        timer.Start();
    }
}
