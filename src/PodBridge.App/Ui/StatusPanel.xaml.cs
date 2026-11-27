using System.Windows;
using System.Windows.Input;
using System.Windows.Shapes;
using PodBridge.Core.Ble;

namespace PodBridge.App.Ui;

public sealed record PanelState(
    string DeviceName,
    bool Learned,
    bool Connected,
    bool InCallMode,
    bool AutoCallMode,
    AirPodsStatus? Battery,
    int? WindowsBattery,
    string? Warning);

/// <summary>Tray popover. Closes when it loses focus, like a menu-bar extra.</summary>
public partial class StatusPanel : Window
{
    private bool _updating, _closing;

    public event Action? ConnectToggled, SettingsClicked, QuitClicked;
    public event Action<bool>? ModeChosen; // true = call

    public StatusPanel()
    {
        InitializeComponent();
        CardChrome.Prepare(this, CardBorder);
        ConnectBtn.Click += (_, _) => ConnectToggled?.Invoke();
        MusicSeg.Checked += (_, _) => { if (!_updating) ModeChosen?.Invoke(false); };
        CallSeg.Checked += (_, _) => { if (!_updating) ModeChosen?.Invoke(true); };
        SettingsBtn.Click += (_, _) => { SettingsClicked?.Invoke(); Dismiss(); };
        QuitBtn.Click += (_, _) => QuitClicked?.Invoke();

        Deactivated += (_, _) => Dismiss();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };
        Loaded += (_, _) =>
        {
            CardPlacement.PlaceNearTray(this, CardChrome.MaterialsAvailable);
            CardPlacement.AnimateIn(this);
            Activate();
        };
    }

    public void Apply(PanelState s)
    {
        _updating = true;
        try
        {
            DeviceText.Text = s.DeviceName;
            Batteries.Update(s.Battery, s.WindowsBattery);

            (string text, string dot) = (s.Learned, s.Connected) switch
            {
                (false, _) => ("Not set up yet", "SecondaryLabelBrush"),
                (_, true) => (s.InCallMode ? "Connected · In a call" : "Connected", "GoodBrush"),
                _ => (s.Battery is null ? "Not connected" : "Nearby · Not connected", "SecondaryLabelBrush"),
            };
            StateText.Text = text;
            StateDot.SetResourceReference(Shape.FillProperty, dot);

            ConnectBtn.Content = s.Connected ? "Disconnect" : "Connect";
            ConnectBtn.Visibility = s.Learned || s.Connected ? Visibility.Visible : Visibility.Collapsed;

            MusicSeg.IsChecked = !s.InCallMode;
            CallSeg.IsChecked = s.InCallMode;
            MusicSeg.IsEnabled = CallSeg.IsEnabled = s.Connected;
            ModeHint.Text = s.InCallMode
                ? "Using the AirPods mic. Sound is mono while the mic is on; that's a Bluetooth limit."
                : s.AutoCallMode ? "High-quality stereo. Switches to the AirPods mic when a call starts."
                                 : "High-quality stereo.";

            Banner.Visibility = s.Warning is null ? Visibility.Collapsed : Visibility.Visible;
            BannerText.Text = s.Warning ?? "";
        }
        finally { _updating = false; }
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        CardPlacement.AnimateOutAndClose(this);
    }
}
