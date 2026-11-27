using System.Windows;
using System.Windows.Input;
using PodBridge.Core.Ble;

namespace PodBridge.App.Ui;

public sealed record PanelState(
    string DeviceName,
    bool Learned,
    bool Connected,
    bool InCallMode,
    string? CallApp,
    AirPodsStatus? Battery,
    string? Warning,
    bool AutoConnect,
    bool ShowPopup,
    bool AutoCallMode,
    bool OpenAtLogin);

/// <summary>Tray popover. Closes when it loses focus, like a menu-bar extra.</summary>
public partial class StatusPanel : Window
{
    private bool _updating, _closing;

    public event Action? ConnectToggled;
    public event Action<bool>? ModeChosen; // true = call
    public event Action<string, bool>? SettingChanged;
    public event Action? LearnClicked, DiagnosticsClicked, BluetoothSettingsClicked, QuitClicked;

    public StatusPanel()
    {
        InitializeComponent();
        ConnectBtn.Click += (_, _) => ConnectToggled?.Invoke();
        MusicSeg.Checked += (_, _) => { if (!_updating) ModeChosen?.Invoke(false); };
        CallSeg.Checked += (_, _) => { if (!_updating) ModeChosen?.Invoke(true); };
        Hook(AutoConnectSw, "AutoConnect");
        Hook(PopupSw, "ShowPopup");
        Hook(CallSw, "AutoCallMode");
        Hook(LoginSw, "OpenAtLogin");
        LearnBtn.Click += (_, _) => { LearnClicked?.Invoke(); Dismiss(); };
        DiagBtn.Click += (_, _) => DiagnosticsClicked?.Invoke();
        BtSettingsBtn.Click += (_, _) => { BluetoothSettingsClicked?.Invoke(); Dismiss(); };
        QuitBtn.Click += (_, _) => QuitClicked?.Invoke();

        Deactivated += (_, _) => Dismiss();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };
        Loaded += (_, _) =>
        {
            CardPlacement.PlaceNearTray(this);
            CardPlacement.AnimateIn(this);
            Activate();
        };
    }

    private void Hook(System.Windows.Controls.CheckBox box, string key)
    {
        box.Checked += (_, _) => { if (!_updating) SettingChanged?.Invoke(key, true); };
        box.Unchecked += (_, _) => { if (!_updating) SettingChanged?.Invoke(key, false); };
    }

    public void Apply(PanelState s)
    {
        _updating = true;
        try
        {
            DeviceText.Text = s.DeviceName;
            Batteries.Update(s.Battery);

            if (!s.Learned)
            {
                StateText.Text = "Not set up yet";
                StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SecondaryLabelBrush");
            }
            else if (s.Connected)
            {
                StateText.Text = s.InCallMode ? $"Connected · In a call" : "Connected";
                StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "GoodBrush");
            }
            else
            {
                StateText.Text = s.Battery is null ? "Not connected" : "Nearby · Not connected";
                StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SecondaryLabelBrush");
            }

            ConnectBtn.Content = s.Connected ? "Disconnect" : "Connect";
            ConnectBtn.Visibility = s.Learned || s.Connected ? Visibility.Visible : Visibility.Collapsed;

            MusicSeg.IsChecked = !s.InCallMode;
            CallSeg.IsChecked = s.InCallMode;
            MusicSeg.IsEnabled = CallSeg.IsEnabled = s.Connected;
            ModeHint.Text = s.InCallMode
                ? "Using the AirPods mic. Sound is mono while the mic is on; that's a Bluetooth limit."
                : s.AutoCallMode
                    ? "High-quality stereo. Switches to the AirPods mic automatically when a call starts."
                    : "High-quality stereo.";

            Banner.Visibility = s.Warning is null ? Visibility.Collapsed : Visibility.Visible;
            BannerText.Text = s.Warning ?? "";

            AutoConnectSw.IsChecked = s.AutoConnect;
            PopupSw.IsChecked = s.ShowPopup;
            CallSw.IsChecked = s.AutoCallMode;
            LoginSw.IsChecked = s.OpenAtLogin;
            LearnBtn.Content = s.Learned ? "Set Up Different AirPods…" : "Set Up My AirPods…";
        }
        finally { _updating = false; }
    }

    public void FlashDiagnosticsCopied()
    {
        DiagBtn.Content = "Copied ✓";
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        t.Tick += (_, _) => { t.Stop(); DiagBtn.Content = "Copy Diagnostics"; };
        t.Start();
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        CardPlacement.AnimateOutAndClose(this);
    }
}
