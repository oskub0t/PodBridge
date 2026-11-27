using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PodBridge.App.Platform;
using PodBridge.App.Ui;
using PodBridge.Core;
using PodBridge.Core.Audio;
using PodBridge.Core.Ble;
using PodBridge.Core.Calls;
using PodBridge.Core.Features;

namespace PodBridge.App;

/// <summary>
/// Wires the pieces together. Everything runs on the UI thread; BLE events are marshalled in.
///
///   case opens → advert → PresenceTracker.Appeared → card + (auto) Bluetooth reconnect
///   endpoint becomes active → music mode (stereo default output)
///   Teams takes the mic → call mode (hands-free output + AirPods mic as defaults)
///   Teams releases the mic → music mode again
///   AirPod out / back in → pause / resume (ear detection, from the advert's in-ear flags)
///   worn elsewhere + this PC starts audio → "Move to This PC"
/// </summary>
internal sealed class AppController : IDisposable
{
    private const short LearnRssi = -60;                 // "right next to the PC" for set-up prompts
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ManualDisconnectGrace = TimeSpan.FromMinutes(2);

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly PodBridgeConfig _cfg = ConfigStore.Load();
    private readonly WindowsAudio _audio = new();
    private readonly BleScanner _ble = new();
    private readonly RegistryMicUsageSource _micUsage = new();
    private readonly PresenceTracker _presence;
    private readonly CallDetector _calls;
    private readonly AudioModeController _mode;
    private readonly MicWatchdog _watchdog = new();
    private readonly MediaSessions _media = new();
    private readonly EarDetection _ear = new();
    private readonly LowBatteryMonitor _lowBattery = new();
    private readonly SwitchAdvisor _switch = new();
    private readonly TrayIcon _tray = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly Dictionary<ushort, AirPodsStatus> _seen = [];
    private readonly Dictionary<ushort, DateTimeOffset> _learnPrompted = [];
    private readonly ModelSightings _sightings = new();
    private int? _windowsBattery;
    private DateTimeOffset _windowsBatteryAt = DateTimeOffset.MinValue;
    private readonly HashSet<ushort> _rejected = [];

    private ConnectCard? _card;
    private StatusPanel? _panel;
    private SettingsWindow? _settings;
    private DateTimeOffset _panelClosedAt;
    private ResolvedEndpoints? _endpoints;
    private bool _connected, _manualCallMode, _learnMode;
    private DateTimeOffset? _connectRequestedAt;
    private DateTimeOffset _manualDisconnectAt = DateTimeOffset.MinValue;
    private string? _bleWarning, _micWarning;

    public AppController()
    {
        _presence = new PresenceTracker(() => _cfg);
        _calls = new CallDetector(() => _cfg);
        _mode = new AudioModeController(_audio, () => _cfg, Log.Write);
        _learnMode = _cfg.MyModelId is null;

        _presence.Appeared += OnCaseOpened;
        _presence.Updated += s =>
        {
            _card?.UpdateStatus(s);
            HandleEars(s);
            HandleBattery(s);
            RefreshUi();
        };
        _presence.Candidate += OnCandidate;
        _calls.CallStarted += OnCallStarted;
        _calls.CallEnded += OnCallEnded;

        _ble.Advertisement += s => _ui.BeginInvoke(() => OnAdvertisement(s));
        _ble.Failed += msg => _ui.BeginInvoke(() => { _bleWarning = msg; Log.Write(msg); RefreshUi(); });

        _media.PlaybackStarted += () => _ui.BeginInvoke(() => OnPcAudioStarted(null));
        CardChrome.UseMaterials = _cfg.UseSystemMaterials;

        _tray.Clicked += TogglePanel;
        _tray.QuitRequested += Quit;
        _tray.SettingsRequested += OpenSettings;
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>
    /// What you named them (the Bluetooth name Windows shows, e.g. "Sam's AirPods Pro"), as on
    /// Apple devices. Falls back to the model name only if it's one we actually recognise.
    /// </summary>
    private string DeviceTitle =>
        _endpoints?.DeviceName ?? _cfg.DeviceName ?? ModelName(_cfg.MyModelId) ?? "AirPods";

    private string? ModelName(ushort? id) =>
        id is ushort m && (AirPodsModels.IsKnown(m) || _cfg.ParsedModelNames().ContainsKey(m))
            ? AirPodsModels.NameFor(m, _cfg.ParsedModelNames())
            : null;

    public void Start(bool showPanel /* false when started at login */)
    {
        Log.Write($"PodBridge {typeof(AppController).Assembly.GetName().Version} starting");
        _ = _media.InitAsync();
        try { _ble.Start(); }
        catch (Exception ex) { _bleWarning = $"Bluetooth scanning unavailable: {ex.Message}"; Log.Write(_bleWarning); }
        Tick();
        _timer.Start();
        if (showPanel) OpenSettings();
    }

    // ---------------- BLE ----------------

    private void OnAdvertisement(AirPodsStatus s)
    {
        if (s.Rssi >= -85)
        {
            _seen[s.ModelId] = s;
            if (_seen.Count > 64) // random IDs (encrypted format) would otherwise grow this forever
                foreach (var stale in _seen.Values.OrderBy(x => x.Timestamp).Take(_seen.Count - 48).ToList())
                    _seen.Remove(stale.ModelId);
        }
        _presence.OnAdvertisement(s);
    }

    private void OnCandidate(AirPodsStatus s)
    {
        // Once set up, other people's AirPods are simply ignored.
        if (!_learnMode || s.Rssi < LearnRssi || _rejected.Contains(s.ModelId)) return;
        // Only offer IDs that repeat; a value that changes every advert can't identify anything.
        if (!_sightings.Observe(s)) return;
        if (_learnPrompted.TryGetValue(s.ModelId, out var t) && s.Timestamp - t < TimeSpan.FromMinutes(10)) return;
        if (_card is { IsVisible: true }) return;
        _learnPrompted[s.ModelId] = s.Timestamp;

        var card = ShowCard(ModelName(s.ModelId) ?? "AirPods nearby", s, CardState.Learn);
        card.PrimaryClicked += () => Adopt(s);
        card.SecondaryClicked += () => { _rejected.Add(s.ModelId); card.Dismiss(); };
    }

    private void Adopt(AirPodsStatus s)
    {
        _cfg.MyModelId = s.ModelId;
        _learnMode = false;
        _rejected.Clear();
        var paired = EndpointResolver.FindAirPodsDevices(_audio.GetEndpoints());
        if (_cfg.DeviceName is null && paired.Count == 1) _cfg.DeviceName = paired[0];
        ConfigStore.Save(_cfg);
        Log.Write($"Adopted model 0x{s.ModelId:X4}, device '{_cfg.DeviceName ?? "(auto)"}'");
        _card?.Close();
        _card = null;
        OnCaseOpened(s);
    }

    public void StartLearning()
    {
        _learnMode = true;
        _rejected.Clear();
        _learnPrompted.Clear();
        _sightings.Clear();
        Hud.Show("Open your AirPods case", "Hold it close to this PC.", seconds: 4);
    }

    private void OnCaseOpened(AirPodsStatus s)
    {
        Log.Write($"Case opened: L{s.LeftPercent} R{s.RightPercent} C{s.CasePercent} rssi {s.Rssi}");
        UpdateConnection();

        if (_connected)
        {
            if (_cfg.ShowPopup) ShowCard(DeviceTitle, s, CardState.Connected);
            return;
        }
        if (_endpoints is null)
        {
            var card = ShowCard(DeviceTitle, s, CardState.NotPaired);
            card.PrimaryClicked += () => { OpenBluetoothSettings(); card.Dismiss(); };
            return;
        }

        bool auto = _cfg.AutoConnect && DateTimeOffset.Now - _manualDisconnectAt > ManualDisconnectGrace;
        if (auto) Connect();
        if (_cfg.ShowPopup || !auto)
        {
            var card = ShowCard(DeviceTitle, s, _connectRequestedAt is null ? CardState.Connect : CardState.Connecting);
            card.PrimaryClicked += () =>
            {
                switch (card.State)
                {
                    case CardState.Connect or CardState.Failed or CardState.MoveHere: Connect(); break;
                    default: card.Dismiss(); break;
                }
            };
            card.SecondaryClicked += () => { OpenBluetoothSettings(); card.Dismiss(); };
        }
    }

    private ConnectCard ShowCard(string title, AirPodsStatus? s, CardState state, string? detail = null)
    {
        _card?.Close();
        var card = new ConnectCard();
        card.Closed += (_, _) => { if (_card == card) _card = null; };
        _card = card;
        card.Show(title, s, state, detail);
        return card;
    }

    // ---------------- Connection ----------------

    private void Connect()
    {
        _endpoints ??= _mode.Resolve();
        if (_endpoints is null) return;
        var all = _audio.GetEndpoints()
            .Where(e => EndpointResolver.BaseDeviceName(e.Name)
                .Equals(_endpoints.DeviceName, StringComparison.OrdinalIgnoreCase));
        int ok = _audio.RequestConnection(all, connect: true);
        if (ok == 0)
        {
            _card?.SetState(CardState.Failed, "Windows didn't accept the connect request. Try Bluetooth settings.");
            return;
        }
        _connectRequestedAt = DateTimeOffset.Now;
        _card?.SetState(CardState.Connecting);
    }

    private void Disconnect()
    {
        if (_endpoints is null) return;
        var all = _audio.GetEndpoints()
            .Where(e => EndpointResolver.BaseDeviceName(e.Name)
                .Equals(_endpoints.DeviceName, StringComparison.OrdinalIgnoreCase));
        _audio.RequestConnection(all, connect: false);
        _manualDisconnectAt = DateTimeOffset.Now;
    }

    private void UpdateConnection()
    {
        _endpoints = _mode.Resolve();
        bool now = _endpoints?.AnyActive == true;
        if (now == _connected) return;
        _connected = now;

        if (now)
        {
            Log.Write("AirPods connected");
            _connectRequestedAt = null;
            bool call = _manualCallMode || (_cfg.AutoCallMode && _calls.InCall);
            if (call) _mode.ApplyCallMode(); else _mode.ApplyMusicMode();
            if (_card is { IsVisible: true }) _card.SetState(CardState.Connected);
            else Hud.Show($"{DeviceTitle} connected", call ? "Call mode" : null);
        }
        else
        {
            Log.Write("AirPods disconnected");
            _micWarning = null;
            _ear.Reset();
            _media.ForgetPaused();
            _windowsBattery = null;
        }
    }

    // ---------------- Calls ----------------

    private void OnCallStarted(string app)
    {
        Log.Write($"Call started ({app})");
        _watchdog.Reset();
        if (!_cfg.AutoCallMode) return;

        if (_connected)
        {
            _mode.ApplyCallMode();
            Hud.Show("Call mode", $"AirPods mic on for {FriendlyApp(app)}");
        }
        else if (!OnPcAudioStarted($"{FriendlyApp(app)} is using a microphone. Use your AirPods for this call?")
                 && !_presence.IsStale(DateTimeOffset.Now))
        {
            Hud.Show($"{FriendlyApp(app)} is using a mic", "Your AirPods are nearby but not connected.", HudKind.Warning, 5);
        }
        RefreshUi();
    }

    private void OnCallEnded()
    {
        Log.Write("Call ended");
        _micWarning = null;
        if (_mode.InCallMode && !_manualCallMode && _connected)
        {
            _mode.ApplyMusicMode();
            Hud.Show("Music mode", "High-quality stereo restored");
        }
        RefreshUi();
    }

    private static string FriendlyApp(string key)
    {
        if (key.StartsWith("MSTeams", StringComparison.OrdinalIgnoreCase)) return "Teams";
        var file = Path.GetFileNameWithoutExtension(key.Replace('#', '\\'));
        return file.Equals("ms-teams", StringComparison.OrdinalIgnoreCase) ? "Teams" : file;
    }

    // ---------------- Ear detection, battery, switching ----------------

    private void HandleEars(AirPodsStatus s)
    {
        int count = (s.LeftInEar ? 1 : 0) + (s.RightInEar ? 1 : 0);
        var action = _ear.Observe(count, s.Timestamp);
        // Track state always, act only when the PC is the one playing, and never mid-call.
        if (action == EarAction.None || !_cfg.EarDetection || !_connected || _calls.InCall) return;
        Log.Write($"Ear detection: {action} ({count} in ear)");
        _ = action == EarAction.Pause ? _media.PauseAllAsync() : _media.ResumeAsync();
    }

    private void HandleBattery(AirPodsStatus s)
    {
        bool inUse = _connected || s.LeftInEar || s.RightInEar;
        var alert = _lowBattery.Observe(s);
        if (alert is null || !_cfg.LowBatteryAlerts || !inUse) return;
        string which = (alert.Left, alert.Right) switch
        {
            (true, true) => "Both AirPods",
            (true, false) => "Left AirPod",
            _ => "Right AirPod",
        };
        Hud.Show($"{DeviceTitle} battery low", $"{which}: {alert.Percent}% remaining", HudKind.Warning, 6);
        Log.Write($"Low battery alert: {which} {alert.Percent}%");
    }

    /// <returns>True if we offered or started a move.</returns>
    private bool OnPcAudioStarted(string? reason)
    {
        var now = DateTimeOffset.Now;
        var latest = _presence.IsStale(now) ? null : _presence.Latest;
        var decision = _switch.OnPcAudioStarted(_cfg.ConnectToThisPc, latest, _connected,
            now - _manualDisconnectAt < ManualDisconnectGrace, now);
        switch (decision)
        {
            case SwitchDecision.Connect:
                Log.Write("Moving AirPods to this PC (automatic)");
                Hud.Show($"Moving {DeviceTitle} to this PC");
                Connect();
                return true;
            case SwitchDecision.Offer:
                var card = ShowCard(DeviceTitle, latest, CardState.MoveHere, reason);
                card.PrimaryClicked += () =>
                {
                    if (card.State is CardState.MoveHere or CardState.Failed) Connect(); else card.Dismiss();
                };
                card.SecondaryClicked += card.Dismiss;
                return true;
            default:
                return false;
        }
    }

    // ---------------- Timer ----------------

    private void Tick()
    {
        var now = DateTimeOffset.Now;
        try
        {
            UpdateConnection();
            _calls.Tick(_micUsage.Read(), now);

            if (_mode.InCallMode && _endpoints?.Mic is { Active: true } mic &&
                _watchdog.Tick(_audio.GetPeak(mic.Id), now, _cfg.SilentMicWarningSeconds))
            {
                _micWarning = "Your AirPods mic isn't receiving any audio. In Teams, check Settings → Devices → Microphone, " +
                              "and make sure the AirPods haven't switched to your phone.";
                Log.Write("Silent mic warning");
                Hud.Show("AirPods mic is silent", "Teams may be using a different microphone.", HudKind.Warning, 6);
            }

            if (_connected && _endpoints is not null && now - _windowsBatteryAt > TimeSpan.FromSeconds(30))
            {
                _windowsBatteryAt = now;
                _ = RefreshWindowsBatteryAsync(_endpoints.DeviceName);
            }

            if (_connectRequestedAt is DateTimeOffset at && !_connected && now - at > ConnectTimeout)
            {
                _connectRequestedAt = null;
                _card?.SetState(CardState.Failed,
                    "Couldn't connect. If they're playing on your phone, pause there and try again.");
            }
        }
        catch (Exception ex) { Log.Write($"Tick failed: {ex}"); }
        RefreshUi();
    }

    private async Task RefreshWindowsBatteryAsync(string deviceName)
    {
        var value = await WindowsBluetooth.GetBatteryAsync(deviceName);
        if (value == _windowsBattery) return;
        _windowsBattery = value;
        Log.Write($"Windows battery for '{deviceName}': {value?.ToString() ?? "not reported"}");
        RefreshUi();
    }

    // ---------------- UI ----------------

    public void RefreshUi()
    {
        var b = _presence.IsStale(DateTimeOffset.Now) ? null : _presence.Latest;
        var low = (b?.LowestPodPercent ?? (b is null ? _windowsBattery : null)) is int p && p <= 20;
        var tip = b is null
            ? $"{DeviceTitle}: {(_connected ? (_windowsBattery is int w ? $"{w}%" : "connected") : "not connected")}"
            : $"{DeviceTitle}: L {Pct(b.LeftPercent)} · R {Pct(b.RightPercent)} · Case {Pct(b.CasePercent)}";
        _tray.Update(_connected, low, tip);

        _panel?.Apply(new PanelState(
            DeviceName: DeviceTitle,
            Learned: _cfg.MyModelId is not null,
            Connected: _connected,
            InCallMode: _mode.InCallMode,
            AutoCallMode: _cfg.AutoCallMode,
            Battery: b,
            WindowsBattery: _windowsBattery,
            Warning: _bleWarning ?? _micWarning));

        _settings?.ApplyDevice(new DeviceInfo(
            DeviceTitle, _cfg.MyModelId, _endpoints?.DeviceName, _connected, _mode.InCallMode, b, _windowsBattery));
    }

    private static string Pct(int? v) => v is int x ? $"{x}%" : "–";

    private void TogglePanel()
    {
        if (_panel is not null) { _panel.Dismiss(); return; }
        // A tray click right after the panel closed itself (it lost focus to the taskbar) means "close".
        if (DateTimeOffset.Now - _panelClosedAt < TimeSpan.FromMilliseconds(350)) return;

        var panel = new StatusPanel();
        panel.Closed += (_, _) => { _panel = null; _panelClosedAt = DateTimeOffset.Now; };
        panel.ConnectToggled += () => { if (_connected) Disconnect(); else Connect(); };
        panel.ModeChosen += call =>
        {
            _manualCallMode = call;
            if (call) _mode.ApplyCallMode(); else _mode.ApplyMusicMode();
            RefreshUi();
        };
        panel.SettingsClicked += OpenSettings;
        panel.QuitClicked += Quit;
        _panel = panel;
        RefreshUi();
        panel.Show();
    }

    public void OpenSettings()
    {
        if (_settings is not null) { _settings.Activate(); return; }
        var w = new SettingsWindow(_cfg);
        w.Closed += (_, _) => _settings = null;
        w.ConfigChanged += () => ConfigStore.Save(_cfg);
        w.ConnectToggled += () => { if (_connected) Disconnect(); else Connect(); };
        w.LearnClicked += StartLearning;
        w.DiagnosticsClicked += CopyDiagnostics;
        w.BluetoothSettingsClicked += OpenBluetoothSettings;
        _settings = w;
        RefreshUi();
        w.Show();
        w.Activate();
    }

    private static void OpenBluetoothSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });

    private void CopyDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"PodBridge {typeof(AppController).Assembly.GetName().Version} on {Environment.OSVersion}");
        sb.AppendLine($"BLE running: {_ble.IsRunning}  warning: {_bleWarning ?? "none"}");
        sb.AppendLine($"Connected: {_connected}  call mode: {_mode.InCallMode}  in call: {_calls.InCall} ({_calls.CallApp})");
        sb.AppendLine().AppendLine("Config:").AppendLine(JsonSerializer.Serialize(_cfg));

        sb.AppendLine().AppendLine("Audio endpoints:");
        foreach (var e in _audio.GetEndpoints())
            sb.AppendLine($"  [{e.Flow}] {(e.Active ? "active " : "offline")}  {e.Name}");

        sb.AppendLine().AppendLine("Defaults:");
        var names = _audio.GetEndpoints().ToDictionary(e => e.Id, e => e.Name);
        foreach (var flow in Enum.GetValues<Flow>())
            foreach (var role in Enum.GetValues<Role>())
            {
                var id = _audio.GetDefaultId(flow, role);
                sb.AppendLine($"  {flow}/{role}: {(id is not null && names.TryGetValue(id, out var n) ? n : id ?? "none")}");
            }

        sb.AppendLine().AppendLine($"Resolved: music={_endpoints?.MusicOutput?.Name} call={_endpoints?.CallOutput?.Name} mic={_endpoints?.Mic?.Name}");

        sb.AppendLine().AppendLine("Apple audio devices heard recently (model, name, signal, age):");
        foreach (var s in _seen.Values.OrderByDescending(x => x.Rssi).Take(20))
            sb.AppendLine($"  0x{s.ModelId:X4}  {AirPodsModels.NameFor(s.ModelId, _cfg.ParsedModelNames()),-24} {s.Rssi} dBm  " +
                          $"{(DateTimeOffset.Now - s.Timestamp).TotalSeconds:0}s ago{(s.ModelId == _cfg.MyModelId ? "  ← yours" : "")}");

        sb.AppendLine().AppendLine($"Windows-reported battery: {_windowsBattery?.ToString() ?? "none"}");
        sb.AppendLine().AppendLine("Raw advertisement bytes (latest per model, strongest first):");
        foreach (var s in _seen.Values.OrderByDescending(x => x.Rssi).Take(12))
            sb.AppendLine($"  0x{s.ModelId:X4}  {s.Rssi} dBm  {s.RawHex}");

        sb.AppendLine().AppendLine("Apps using the microphone now:");
        foreach (var m in _micUsage.Read().Where(m => m.InUse))
            sb.AppendLine($"  {m.AppKey}");

        var text = sb.ToString();
        Log.Write("Diagnostics:\n" + text);
        try { Clipboard.SetText(text); } catch (Exception ex) { Log.Write($"Clipboard failed: {ex.Message}"); }
    }

    private void Quit()
    {
        _timer.Stop();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _timer.Stop();
        _ble.Dispose();
        _tray.Dispose();
    }
}
