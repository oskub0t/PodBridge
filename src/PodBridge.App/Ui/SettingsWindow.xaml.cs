using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PodBridge.App.Platform;
using PodBridge.Core;
using PodBridge.Core.Ble;
using PodBridge.Core.Features;

namespace PodBridge.App.Ui;

public sealed record DeviceInfo(
    string Title, ushort? ModelId, string? BluetoothName, bool Connected, bool InCallMode, AirPodsStatus? Battery);

/// <summary>
/// The main window, modelled on the AirPods pane in macOS System Settings: hero, battery, then
/// grouped settings in plain language. It uses WPF's Windows 11 (Fluent) theme, so the title bar,
/// Mica background, scroll bars, combo box and buttons are native Windows controls. That matches how
/// Apple's own Windows apps look.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly SwitchPolicy[] PolicyOrder = [SwitchPolicy.Automatically, SwitchPolicy.Ask, SwitchPolicy.Never];
    private readonly PodBridgeConfig _cfg;
    private bool _loading;

    public event Action? ConfigChanged, ConnectToggled, LearnClicked, DiagnosticsClicked, BluetoothSettingsClicked;

    public SettingsWindow(PodBridgeConfig cfg)
    {
#pragma warning disable WPF0001 // Fluent theme is still flagged experimental in .NET 10
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        InitializeComponent();
        _cfg = cfg;
        LoadSettings();

        ConnectBtn.Click += (_, _) => ConnectToggled?.Invoke();
        LearnBtn.Click += (_, _) => LearnClicked?.Invoke();
        DiagBtn.Click += (_, _) =>
        {
            DiagnosticsClicked?.Invoke();
            DiagBtn.Content = "Copied ✓";
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            t.Tick += (_, _) => { t.Stop(); DiagBtn.Content = "Copy Diagnostics"; };
            t.Start();
        };
        BtSettingsBtn.Click += (_, _) => BluetoothSettingsClicked?.Invoke();

        SwitchCombo.SelectionChanged += (_, _) => Change(() => _cfg.ConnectToThisPc = PolicyOrder[Math.Max(0, SwitchCombo.SelectedIndex)]);
        Bind(AutoConnectSw, v => _cfg.AutoConnect = v);
        Bind(PopupSw, v => _cfg.ShowPopup = v);
        Bind(EarSw, v => _cfg.EarDetection = v);
        Bind(CallSw, v => _cfg.AutoCallMode = v);
        Bind(SilentMicSw, v => _cfg.SilentMicWarningSeconds = v ? 20 : 0);
        Bind(LowBatterySw, v => _cfg.LowBatteryAlerts = v);
        Bind(MaterialsSw, v => { _cfg.UseSystemMaterials = v; CardChrome.UseMaterials = v; });
        LoginSw.Checked += (_, _) => { if (!_loading) Autostart.Set(true); };
        LoginSw.Unchecked += (_, _) => { if (!_loading) Autostart.Set(false); };
    }

    private void LoadSettings()
    {
        _loading = true;
        SwitchCombo.SelectedIndex = Array.IndexOf(PolicyOrder, _cfg.ConnectToThisPc);
        AutoConnectSw.IsChecked = _cfg.AutoConnect;
        PopupSw.IsChecked = _cfg.ShowPopup;
        EarSw.IsChecked = _cfg.EarDetection;
        CallSw.IsChecked = _cfg.AutoCallMode;
        SilentMicSw.IsChecked = _cfg.SilentMicWarningSeconds > 0;
        LowBatterySw.IsChecked = _cfg.LowBatteryAlerts;
        LoginSw.IsChecked = Autostart.IsEnabled;
        MaterialsSw.IsChecked = _cfg.UseSystemMaterials;
        MaterialsSw.IsEnabled = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        _loading = false;
    }

    private void Bind(CheckBox box, Action<bool> set)
    {
        box.Checked += (_, _) => Change(() => set(true));
        box.Unchecked += (_, _) => Change(() => set(false));
    }

    private void Change(Action apply)
    {
        if (_loading) return;
        apply();
        ConfigChanged?.Invoke();
    }

    public void ApplyDevice(DeviceInfo d)
    {
        Title = d.Title;
        NameText.Text = d.Title;
        StateText.Text = d.ModelId is null ? "Not set up yet"
            : d.Connected ? (d.InCallMode ? "Connected · In a call" : "Connected")
            : d.Battery is null ? "Not connected" : "Nearby · Not connected";
        ConnectBtn.Content = d.Connected ? "Disconnect" : "Connect";
        ConnectBtn.Visibility = d.ModelId is null ? Visibility.Collapsed : Visibility.Visible;
        Batteries.Update(d.Battery);
        ModelText.Text = d.ModelId is ushort id ? $"{AirPodsModels.NameFor(id, _cfg.ParsedModelNames())} (0x{id:X4})" : "–";
        BtNameText.Text = d.BluetoothName ?? "–";
    }
}
