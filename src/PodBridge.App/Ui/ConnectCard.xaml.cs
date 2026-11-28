using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PodBridge.Core.Ble;

namespace PodBridge.App.Ui;

public enum CardState { Learn, Connect, Connecting, Connected, NotPaired, Failed, MoveHere }

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
        CardChrome.Prepare(this, CardBorder);
        if (Artwork.Fancy) HeroImage.Visibility = Visibility.Visible;
        CloseBtn.Click += (_, _) => Dismiss();
        PrimaryBtn.Click += (_, _) => PrimaryClicked?.Invoke();
        SecondaryBtn.Click += (_, _) => SecondaryClicked?.Invoke();
        _autoClose.Tick += (_, _) => { if (!IsMouseOver) Dismiss(); };
        MouseLeave += (_, _) => RestartTimer();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };
        Loaded += (_, _) =>
        {
            CardPlacement.AnchorNearTray(this, CardChrome.MaterialsAvailable);
            CardPlacement.AnimateIn(this);
        };
    }

    public CardState State { get; private set; }

    public void Show(string title, BatteryView? battery, CardState state, string? detail = null)
    {
        TitleText.Text = title;
        Batteries.Update(battery);
        if (Artwork.Fancy && HeroImage.Source is null)
        {
            // The case just opened: play "lid opens, buds rise". Other reasons get the still.
            if (state is CardState.MoveHere or CardState.Failed || !Artwork.HasFrames) HeroImage.Source = Artwork.Hero;
            else Artwork.PlayCaseOpen(HeroImage);
        }
        SetState(state, detail);
        if (!IsVisible) Show();
    }

    public void UpdateBattery(BatteryView battery) => Batteries.Update(battery);

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
                SubtitleText.Text = detail ?? "Not connected";
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
            case CardState.MoveHere:
                SubtitleText.Text = detail ?? "This PC is playing audio. Use your AirPods here?";
                PrimaryBtn.Content = "Move to This PC";
                SecondaryBtn.Content = "Not Now";
                SecondaryBtn.Visibility = Visibility.Visible;
                autoCloseSeconds = 15;
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
