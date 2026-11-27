using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PodBridge.Core.Ble;

namespace PodBridge.App.Ui;

public enum CardState { Learn, Connect, Connecting, Connected, NotPaired, Failed }

/// <summary>
/// The card that appears when you open the case. One clear primary action per state, short text,
/// battery front and centre. It never steals keyboard focus (you may be typing), and it dismisses
/// itself unless you are hovering it.
/// </summary>
public partial class ConnectCard : Window
{
    private readonly DispatcherTimer _autoClose = new();
    private bool _closing;

    public event Action? PrimaryClicked;
    public event Action? SecondaryClicked;

    public ConnectCard()
    {
        InitializeComponent();
        CloseBtn.Click += (_, _) => Dismiss();
        PrimaryBtn.Click += (_, _) => PrimaryClicked?.Invoke();
        SecondaryBtn.Click += (_, _) => SecondaryClicked?.Invoke();
        _autoClose.Tick += (_, _) => { if (!IsMouseOver) Dismiss(); };
        MouseLeave += (_, _) => RestartTimer();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };
        Loaded += (_, _) =>
        {
            CardPlacement.PlaceNearTray(this);
            CardPlacement.AnimateIn(this);
        };
    }

    public CardState State { get; private set; }

    public void Show(string title, AirPodsStatus? status, CardState state)
    {
        TitleText.Text = title;
        Batteries.Update(status);
        SetState(state);
        if (!IsVisible) Show();
    }

    public void UpdateStatus(AirPodsStatus status) => Batteries.Update(status);

    public void SetState(CardState state, string? detail = null)
    {
        State = state;
        SecondaryBtn.Visibility = Visibility.Collapsed;
        PrimaryBtn.IsEnabled = true;
        var autoCloseSeconds = 12;

        switch (state)
        {
            case CardState.Learn:
                SubtitleText.Text = detail ?? "New AirPods nearby. Are these yours?";
                PrimaryBtn.Content = "These Are Mine";
                SecondaryBtn.Content = "Not My AirPods";
                SecondaryBtn.Visibility = Visibility.Visible;
                autoCloseSeconds = 20;
                break;
            case CardState.Connect:
                SubtitleText.Text = "Not connected";
                PrimaryBtn.Content = "Connect";
                break;
            case CardState.Connecting:
                SubtitleText.Text = "Connecting…";
                PrimaryBtn.Content = "Connecting…";
                PrimaryBtn.IsEnabled = false;
                autoCloseSeconds = 15;
                break;
            case CardState.Connected:
                SubtitleText.Text = detail ?? "Connected";
                PrimaryBtn.Content = "Done";
                autoCloseSeconds = 4;
                break;
            case CardState.NotPaired:
                SubtitleText.Text = "Pair these AirPods with Windows once: hold the button on the case until the light flashes white, then add them in Bluetooth settings.";
                PrimaryBtn.Content = "Open Bluetooth Settings";
                autoCloseSeconds = 30;
                break;
            case CardState.Failed:
                SubtitleText.Text = detail ?? "Couldn't connect. Try again, or use Bluetooth settings.";
                PrimaryBtn.Content = "Try Again";
                SecondaryBtn.Content = "Open Bluetooth Settings";
                SecondaryBtn.Visibility = Visibility.Visible;
                autoCloseSeconds = 20;
                break;
        }
        _autoClose.Interval = TimeSpan.FromSeconds(autoCloseSeconds);
        RestartTimer();
    }

    private void RestartTimer()
    {
        _autoClose.Stop();
        _autoClose.Start();
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _autoClose.Stop();
        CardPlacement.AnimateOutAndClose(this);
    }
}
