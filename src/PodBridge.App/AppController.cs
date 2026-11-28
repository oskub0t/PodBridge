using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PodBridge.App.Platform;
using PodBridge.App.Ui;
using PodBridge.Core;
using PodBridge.Core.Aap;
using PodBridge.Core.Audio;
using PodBridge.Core.Ble;
using PodBridge.Core.Calls;
using PodBridge.Core.Features;
using PodBridge.Core.Updates;

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
    private readonly BleScanner _ble;

    // Mic-usage record: read when Windows reports a change (plus a slow safety check), not every second.
    private RegistryWatcher? _micWatcher;
    private IReadOnlyList<MicUsageEntry> _micEntries = [];
    private volatile bool _micDirty = true;
    private DateTimeOffset _micReadAt = DateTimeOffset.MinValue;

    // Performance figures for diagnostics, refreshed once a minute.
    private readonly System.Diagnostics.Process _self = System.Diagnostics.Process.GetCurrentProcess();
    private DateTimeOffset _perfAt = DateTimeOffset.Now;
    private TimeSpan _perfCpu;
    private (long Delivered, long Apple, long Parsed) _perfCounts;
    private long _myAdverts, _perfMyAdverts;
    private string _perfSummary = "measuring (first figures after one minute)";
    private readonly RegistryMicUsageSource _micUsage = new();
    private readonly PresenceTracker _presence;
    private readonly CallDetector _calls;
    private readonly AudioModeController _mode;
    private readonly MicWatchdog _watchdog = new();
    private readonly MediaSessions _media = new();
    private readonly EarDetection _ear = new();
    private readonly EarStateMerger _earMerger = new();   // both pods' views → one in-ear count
    private readonly LowBatteryMonitor _lowBattery = new();
    private readonly SwitchAdvisor _switch = new();
    private readonly TrayIcon _tray = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Updates (GitHub Releases; the app's only network use, once a day if enabled)
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private ReleaseInfo? _availableUpdate;
    private bool _updateBusy;
    private string? _updateStatus;

    // Case-open cards close when the lid closes, like on iOS: the AirPods stop advertising then.
    private readonly AdvertCadence _lidCadence = new();             // fallback: adverts stopped
    private readonly Dictionary<ushort, LidTracker> _lids = [];     // primary: the lid byte
    private readonly DispatcherTimer _lidTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    // Connect when a bud goes in (default) or when the case opens; sound follows your ears.
    private readonly AutoConnectPolicy _autoConnect = new();
    private bool _waitingForEars;              // connected while in the case: audio not switched yet
    private string? _outputBeforeAirPods;      // what to keep playing on until a bud is in
    private ushort? _cardCaseModel;

    private readonly Dictionary<ushort, AirPodsStatus> _seen = [];
    private readonly Dictionary<ushort, DateTimeOffset> _learnPrompted = [];
    private readonly ModelSightings _sightings = new();
    private readonly BatteryMemory _battery = BatteryStore.Load();

    // Recent changes of the in-ear status byte, for diagnosing the in-ear mapping on new models.
    private readonly Queue<string> _earLog = new();
    private string? _lastEarSignature;
    private string _lastLidText = "?";

    // Optional advanced features (driver installed separately with admin; off by default).
    private AapSession? _aap;
    private bool _driverInstalled;
    private DateTimeOffset _driverCheckAt = DateTimeOffset.MinValue;
    private DateTimeOffset _aapRetryAt = DateTimeOffset.MinValue;
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
        _ble = new BleScanner(_cfg.BluetoothFilter);
        _perfCpu = _self.TotalProcessorTime;
        try
        {
            _micWatcher = new RegistryWatcher(Microsoft.Win32.Registry.CurrentUser, RegistryMicUsageSource.Root);
            _micWatcher.Changed += () => { _micDirty = true; _ui.BeginInvoke(CheckCalls); };
        }
        catch (Exception ex) { Log.Write($"Mic watcher unavailable, polling instead: {ex.Message}"); }
        _presence = new PresenceTracker(() => _cfg);
        _calls = new CallDetector(() => _cfg);
        _mode = new AudioModeController(_audio, () => _cfg, Log.Write);
        if (_cfg.MyModelId is ushort saved && !AirPodsModels.LooksValid(saved))
        {
            // v0.2.0 could save an ID read from an encrypted advert; it never matches again.
            Log.Write($"Discarding invalid saved model ID 0x{saved:X4}; setup will run again");
            _cfg.MyModelId = null;
            ConfigStore.Save(_cfg);
        }
        _learnMode = _cfg.MyModelId is null;

        _presence.Appeared += s =>
        {
            _earMerger.Reset(); // back after an absence: old in-ear state no longer applies
            OnCaseOpened(s);
        };
        _presence.Updated += s =>
        {
            _myAdverts++;
            // While the advanced session reports exact (1 %) battery, don't overwrite it with 10 % steps.
            if (_aap?.Battery is null && _battery.Observe(s)) BatteryStore.Save(_battery);
            _card?.UpdateBattery(_battery.View(DateTimeOffset.Now));
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
        Artwork.Fancy = _cfg.FancyArtwork;
        if (Artwork.Fancy) _ = Artwork.PreloadAsync(); // decode the animation frames in the background

        _tray.Clicked += TogglePanel;
        _tray.QuitRequested += Quit;
        _tray.SettingsRequested += OpenSettings;
        _timer.Tick += (_, _) => Tick();
        _lidTimer.Tick += (_, _) => CheckLid();
    }

    /// <summary>
    /// What you named them (the Bluetooth name Windows shows, e.g. "Sam's AirPods Pro"), as on
    /// Apple devices. Falls back to the model name only if it's one we actually recognise.
    /// </summary>
    private string DeviceTitle =>
        (_endpoints?.DeviceName ?? _cfg.DeviceName) is string name
            ? EndpointResolver.DisplayName(name)
            : ModelName(_cfg.MyModelId) ?? "AirPods";

    private string? ModelName(ushort? id) =>
        id is ushort m && (AirPodsModels.IsKnown(m) || _cfg.ParsedModelNames().ContainsKey(m))
            ? AirPodsModels.NameFor(m, _cfg.ParsedModelNames())
            : null;

    public void Start(bool showPanel /* false when started at login */, bool justUpdated = false)
    {
        Log.Write($"PodBridge {typeof(AppController).Assembly.GetName().Version} starting" +
                  $" ({(Installer.IsRunningInstalled ? "installed" : "not installed")})");
        Notifications.Activated += OnNotificationClicked;
        if (justUpdated) Hud.Show($"PodBridge updated to {Updater.CurrentVersion.ToString(3)}");
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
        if (s.ModelId == _cardCaseModel) _lidCadence.Observe(s.Timestamp);
        HandleLid(s);
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
        if (!AirPodsModels.LooksValid(s.ModelId)) return;
        // Setup asks you to open the case next to the PC, and only an open case reports its own
        // battery. That tells your AirPods apart from a colleague's pair that's just being worn nearby.
        if (s.CasePercent is null) return;
        // Only offer IDs that repeat; a value that changes every advert can't identify anything.
        if (!_sightings.Observe(s)) return;
        if (_learnPrompted.TryGetValue(s.ModelId, out var t) && s.Timestamp - t < TimeSpan.FromMinutes(10)) return;
        if (_card is { IsVisible: true }) return;
        _learnPrompted[s.ModelId] = s.Timestamp;

        var card = ShowCard(ModelName(s.ModelId) ?? "AirPods nearby", BatteryView.Live(s), CardState.Learn, caseModel: s.ModelId);
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
        Hud.Show("Open your AirPods case", "Keep the AirPods inside and hold it next to this PC.", seconds: 5);
    }

    private void OnCaseOpened(AirPodsStatus s)
    {
        _autoConnect.ResetBaseline(); // the reading right after the lid opens is a baseline, not "went in"
        Log.Write($"Case opened: L{s.LeftPercent} R{s.RightPercent} C{s.CasePercent} rssi {s.Rssi}");
        UpdateConnection();

        if (_connected)
        {
            if (_cfg.ShowPopup) ShowCard(DeviceTitle, MyBattery(), CardState.Connected, caseModel: s.ModelId);
            return;
        }
        if (_endpoints is null)
        {
            var card = ShowCard(DeviceTitle, MyBattery(), CardState.NotPaired, caseModel: s.ModelId);
            card.PrimaryClicked += () => { OpenBluetoothSettings(); card.Dismiss(); };
            return;
        }

        bool auto = _autoConnect.OnCaseOpened(_cfg, _connected, RecentlyDisconnected, DateTimeOffset.Now);
        if (auto) Connect();
        bool waitsForEar = _cfg.AutoConnect && _cfg.ConnectWhen == ConnectTrigger.PutInEar;
        if (_cfg.ShowPopup || !_cfg.AutoConnect)
        {
            var card = ShowCard(DeviceTitle, MyBattery(), _connectRequestedAt is null ? CardState.Connect : CardState.Connecting,
                waitsForEar ? "Put an AirPod in to connect." : null, caseModel: s.ModelId);
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

    /// <summary>Your AirPods' battery: live parts plus remembered ones. Null until set up.</summary>
    private BatteryView? MyBattery() =>
        _cfg.MyModelId is ushort id && _battery.ModelId == id ? _battery.View(DateTimeOffset.Now) : null;

    /// <param name="caseModel">Set for cards that appeared because a case opened: they close when that
    /// case's lid closes. Null for cards with other reasons (e.g. "Move to This PC").</param>
    private ConnectCard ShowCard(string title, BatteryView? battery, CardState state, string? detail = null,
        ushort? caseModel = null)
    {
        _cardCaseModel = caseModel;
        _lidCadence.Reset();
        if (caseModel is ushort m && _seen.TryGetValue(m, out var first)) _lidCadence.Observe(first.Timestamp);
        if (caseModel is not null) _lidTimer.Start();
        _card?.Close();
        var card = new ConnectCard();
        card.Closed += (_, _) => { if (_card == card) _card = null; };
        _card = card;
        card.Show(title, battery, state, detail);
        return card;
    }

    /// <summary>
    /// Lid byte: close the case card the moment the lid closes, and show it again on every real
    /// opening (not just after 30 s of silence), like iOS.
    /// </summary>
    private void HandleLid(AirPodsStatus s)
    {
        bool mine = _presence.IsMine(s);
        if (!mine && s.ModelId != _cardCaseModel) return;
        if (!_lids.TryGetValue(s.ModelId, out var lid)) _lids[s.ModelId] = lid = new LidTracker();

        // After an absence the "case appeared" path shows the card; start this tracker fresh.
        if (mine && _presence.IsStale(s.Timestamp)) lid.Reset();

        var ev = lid.Observe(s);
        if (mine && lid.LastIgnored is string why) EarLog($"lid report ignored: {why}", s.Timestamp);
        switch (ev)
        {
            case LidEvent.Closed when _card is { IsVisible: true } card && _cardCaseModel == s.ModelId:
                Log.Write("Lid closed: dismissing card");
                if (mine) EarLog("LID CLOSED → card dismissed", s.Timestamp);
                _lidTimer.Stop();
                _cardCaseModel = null;
                card.Dismiss();
                break;
            case LidEvent.Closed when mine:
                EarLog("LID CLOSED (no card open)", s.Timestamp);
                break;
            case LidEvent.Opened when mine && s.InEarCount == 0:
                Log.Write("Lid opened");
                EarLog("LID OPENED → showing card", s.Timestamp);
                OnCaseOpened(s);
                break;
            case LidEvent.Opened when mine:
                EarLog($"LID OPENED, no card ({s.InEarCount} in ear)", s.Timestamp);
                break;
        }
    }

    private void CheckLid()
    {
        if (_card is not { IsVisible: true } card || _cardCaseModel is null) { _lidTimer.Stop(); return; }
        if (!_lidCadence.LooksGone(DateTimeOffset.Now)) return;
        Log.Write($"Case closed: dismissing card (no adverts for {_lidCadence.Threshold.TotalMilliseconds:0} ms)");
        _lidTimer.Stop();
        _cardCaseModel = null;
        card.Dismiss();
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
            bool inCase = _cfg.EarDetection && !call && _ear.InEarCount == 0 && !_presence.IsStale(DateTimeOffset.Now);
            if (inCase)
            {
                // Like Apple: connected early is fine, but sound only moves once a bud is in.
                _waitingForEars = true;
                KeepOutputOffAirPods();
                Log.Write("Connected while in the case: keeping sound on this PC until a bud is in");
            }
            else if (call) _mode.ApplyCallMode();
            else _mode.ApplyMusicMode();

            if (_card is { IsVisible: true })
                _card.SetState(CardState.Connected, inCase ? "Connected. Put them in to listen." : null);
            else if (!inCase) Hud.Show($"{DeviceTitle} connected", call ? "Call mode" : null);
        }
        else
        {
            Log.Write("AirPods disconnected");
            _micWarning = null;
            _waitingForEars = false;
            _ear.Reset();
            _media.ForgetPaused();
            StopAap();
            _windowsBattery = null;
        }
    }

    private bool RecentlyDisconnected => DateTimeOffset.Now - _manualDisconnectAt < ManualDisconnectGrace;

    private bool IsAirPodsEndpoint(string? id) =>
        id is not null && _endpoints is { } r &&
        (r.MusicOutput?.Id == id || r.CallOutput?.Id == id || r.Mic?.Id == id);

    /// <summary>Remembers the PC's own output while the AirPods aren't connected.</summary>
    private void RememberPcOutput()
    {
        var id = _audio.GetDefaultId(Flow.Render, Role.Console);
        if (id is not null && !IsAirPodsEndpoint(id)) _outputBeforeAirPods = id;
    }

    /// <summary>If Windows switched the output to the AirPods on connect, put it back for now.</summary>
    private void KeepOutputOffAirPods()
    {
        if (_outputBeforeAirPods is null) return;
        foreach (var role in new[] { Role.Console, Role.Multimedia })
            if (IsAirPodsEndpoint(_audio.GetDefaultId(Flow.Render, role)))
                _audio.SetDefault(_outputBeforeAirPods, role);
    }

    /// <summary>A bud went in: connect if that's the trigger, and move the sound if we were waiting.</summary>
    private void OnEarsChanged(int inEar)
    {
        var now = DateTimeOffset.Now;
        if (_autoConnect.OnInEarCount(_cfg, inEar, _connected, RecentlyDisconnected, now))
        {
            Log.Write($"Auto-connect: {inEar} in ear");
            Connect();
        }
        if (_waitingForEars && _connected && inEar > 0)
        {
            _waitingForEars = false;
            Log.Write("A bud is in: moving sound to the AirPods");
            if (_manualCallMode || (_cfg.AutoCallMode && _calls.InCall)) _mode.ApplyCallMode(); else _mode.ApplyMusicMode();
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
        int count = _earMerger.Observe(s); // a pod in the case only speaks for itself
        // Log when something meaningful changes, not every alternating advert from the two pods.
        if (LidTracker.FromPodInCase(s)) _lastLidText = LidTracker.IsOpen(s) ? "open" : "closed";
        string lid = _lastLidText;
        // Combined picture only: the two pods' alternating adverts would otherwise fill the log.
        string signature = $"{count}{s.LeftCharging}{s.RightCharging}{lid}";
        if (signature != _lastEarSignature)
        {
            _lastEarSignature = signature;
            EarLog($"L {(s.LeftInEar ? "in" : "out")}{(s.LeftCharging ? " (charging)" : "")}, " +
                   $"R {(s.RightInEar ? "in" : "out")}{(s.RightCharging ? " (charging)" : "")} " +
                   $"[from {((s.RawStatus & 0x20) != 0 ? "left" : "right")} pod] → {count} in ear, " +
                   $"lid {lid}   [status 0x{s.RawStatus:X2}, lid 0x{s.RawLid:X2}]", s.Timestamp);
        }
        ActOnEars(_ear.Observe(count, s.Timestamp), "advertisement");
        if (_ear.InEarCount is int stable) OnEarsChanged(stable);
    }

    private void EarLog(string text, DateTimeOffset? at = null)
    {
        var entry = $"{(at ?? DateTimeOffset.Now).LocalDateTime:HH:mm:ss}  {text}";
        _earLog.Enqueue(entry);
        while (_earLog.Count > 80) _earLog.Dequeue();
        Log.Write("Ears: " + entry);
    }

    private void ActOnEars(EarAction action, string source)
    {
        // Track state always, act only when the PC is the one playing, and never mid-call.
        if (action == EarAction.None) return;
        string? skip = !_cfg.EarDetection ? "ear detection is off"
                     : !_connected ? "AirPods not connected to this PC"
                     : _calls.InCall ? "in a call" : null;
        if (skip is not null) { EarLog($"→ would {action.ToString().ToLower()}, skipped: {skip}"); return; }
        _ = RunEarActionAsync(action, source);
    }

    private async Task RunEarActionAsync(EarAction action, string source)
    {
        int n = action == EarAction.Pause ? await _media.PauseAllAsync() : await _media.ResumeAsync();
        EarLog(action == EarAction.Pause
            ? $"→ PAUSE ({_ear.InEarCount} in ear, from {source}): paused {n} player(s){(n == 0 ? " — nothing was playing" : "")}"
            : $"→ RESUME ({_ear.InEarCount} in ear, from {source}): resumed {n} player(s){(n == 0 ? " — nothing paused by PodBridge" : "")}");
    }

    // ---------------- Advanced features (optional driver) ----------------

    private void UpdateAdvanced(DateTimeOffset now)
    {
        if (now - _driverCheckAt > TimeSpan.FromSeconds(5))
        {
            _driverCheckAt = now;
            _driverInstalled = AapDriverTransport.IsDriverInstalled();
        }
        bool wanted = _cfg.AdvancedFeatures && _driverInstalled && _connected;
        if (!wanted) { StopAap(); return; }
        if (_aap is not null || now < _aapRetryAt) return;
        _aapRetryAt = now + TimeSpan.FromSeconds(10); // a failing driver must not be hammered every tick

        var transport = AapDriverTransport.TryOpen();
        if (transport is null) return;
        var session = new AapSession(transport);
        session.BatteryChanged += b => _ui.BeginInvoke(() => OnAapBattery(b));
        session.InEarCountChanged += n => _ui.BeginInvoke(() =>
            ActOnEars(_ear.ObserveExact(n, DateTimeOffset.Now), "AirPods"));
        session.NoiseModeChanged += _ => _ui.BeginInvoke(RefreshUi);
        session.ConversationAwarenessChanged += _ => _ui.BeginInvoke(RefreshUi);
        session.Closed += () => _ui.BeginInvoke(StopAap);
        _aap = session;
        Log.Write("Advanced features: session opened");
        _ = StartAapAsync(session);
    }

    private async Task StartAapAsync(AapSession session)
    {
        try { await session.StartAsync(); }
        catch (Exception ex) { Log.Write($"Advanced features: start failed: {ex.Message}"); StopAap(); }
    }

    private void StopAap()
    {
        if (_aap is null) return;
        _aap.Dispose();
        _aap = null;
        Log.Write("Advanced features: session closed");
        RefreshUi();
    }

    private void OnAapBattery(AapBattery b)
    {
        if (_cfg.MyModelId is not ushort id) return;
        var (l, r, c) = (b.Get(BatteryComponent.Left), b.Get(BatteryComponent.Right), b.Get(BatteryComponent.Case));
        var s = new AirPodsStatus(id, l?.Percent, r?.Percent, c?.Percent,
            l?.State == ChargeState.Charging, r?.State == ChargeState.Charging, c?.State == ChargeState.Charging,
            _ear.InEarCount >= 1, _ear.InEarCount >= 2, 0, DateTimeOffset.Now);
        if (_battery.Observe(s)) BatteryStore.Save(_battery);
        HandleBattery(s);
        RefreshUi();
    }

    private async Task SendAap(Func<AapSession, Task> command)
    {
        if (_aap is null) return;
        try { await command(_aap); }
        catch (Exception ex) { Log.Write($"Advanced features: command failed: {ex.Message}"); }
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
        Notify("battery", $"{DeviceTitle} battery low", $"{which}: {alert.Percent}% remaining", HudKind.Warning);
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
                if (Notifications.Show("move", $"Use {DeviceTitle} with this PC?",
                        reason ?? "This PC is playing audio and you're wearing your AirPods.",
                        ("Move to This PC", "move"), ("Not Now", "dismiss")))
                    return true;
                var card = ShowCard(DeviceTitle, MyBattery(), CardState.MoveHere, reason);
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
            if (!_connected) RememberPcOutput();
            if (!_updateBusy && now - _startedAt > TimeSpan.FromMinutes(1) &&
                UpdatePolicy.ShouldCheck(_cfg.CheckForUpdates, _cfg.LastUpdateCheck, now))
                _ = CheckForUpdatesAsync(manual: false);
            UpdateConnection();
            UpdateAdvanced(now);
            CheckCalls();
            UpdatePerformance(now);

            if (_mode.InCallMode && _endpoints?.Mic is { Active: true } mic &&
                _watchdog.Tick(_audio.GetPeak(mic.Id), now, _cfg.SilentMicWarningSeconds))
            {
                _micWarning = "Your AirPods mic isn't receiving any audio. In Teams, check Settings → Devices → Microphone, " +
                              "and make sure the AirPods haven't switched to your phone.";
                Log.Write("Silent mic warning");
                Notify("mic", "Your AirPods mic is silent",
                    "Teams may be using a different microphone, or the AirPods switched to your phone.", HudKind.Warning);
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
        var now = DateTimeOffset.Now;
        var battery = MyBattery();
        bool nearby = !_presence.IsStale(now);
        var low = (battery?.LowestPodPercent ?? _windowsBattery) is int p && p <= 20;

        string tip;
        if (battery is { HasAny: true } v)
        {
            // Windows caps tray tooltips at 127 characters; TrayIcon.Update truncates if a long name hits it.
            tip = $"{DeviceTitle}: L {Part(v.Left)} · R {Part(v.Right)} · Case {Part(v.Case)}";
        }
        else
        {
            tip = $"{DeviceTitle}: {(_connected ? (_windowsBattery is int w ? $"{w}%" : "connected") : "not connected")}";
        }
        _tray.Update(_connected, low, tip);

        _panel?.Apply(new PanelState(
            DeviceName: DeviceTitle,
            Learned: _cfg.MyModelId is not null,
            Connected: _connected,
            InCallMode: _mode.InCallMode,
            AutoCallMode: _cfg.AutoCallMode,
            Battery: battery,
            Nearby: nearby,
            WindowsBattery: _windowsBattery,
            Warning: _bleWarning ?? _micWarning));

        _settings?.ApplyDevice(new DeviceInfo(
            DeviceTitle, _cfg.MyModelId, _endpoints?.DeviceName, _connected, _mode.InCallMode,
            battery, nearby, _windowsBattery));
        _settings?.ApplyAppState(new AppState(
            Version: Updater.CurrentVersion.ToString(3),
            Installed: Installer.IsInstalled && Installer.IsRunningInstalled,
            UpdateAvailable: _availableUpdate?.Version.ToString(3),
            UpdateBusy: _updateBusy,
            UpdateStatus: _updateStatus ?? (_cfg.LastUpdateCheck is DateTimeOffset t
                ? $"Last checked {t.LocalDateTime:d MMM HH:mm}" : "Not checked yet")));
        _settings?.ApplyAdvanced(new AdvancedState(
            _driverInstalled, _cfg.AdvancedFeatures, _aap is not null, _aap?.NoiseMode, _aap?.ConversationAwareness));
    }

    private static string Pct(int? v) => v is int x ? $"{x}%" : "–";

    private static string Part(PartView p) =>
        p.Age is TimeSpan age && p.Percent is not null ? $"{Pct(p.Percent)} ({BatteryView.FormatAge(age)})" : Pct(p.Percent);

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
        w.CheckUpdatesClicked += () => _ = CheckForUpdatesAsync(manual: true);
        w.UpdateClicked += () => _ = InstallUpdateAsync();
        w.InstallClicked += InstallForUser;
        w.UninstallClicked += StartUninstall;
        w.NotificationSettingsClicked += () => OpenUri("ms-settings:notifications");
        w.AdvancedHelpClicked += () => Process.Start(new ProcessStartInfo(
            "https://github.com/oskub0t/PodBridge/blob/main/docs/advanced-features.md") { UseShellExecute = true });
        w.NoiseModeChosen += m => _ = SendAap(a => a.SetNoiseModeAsync(m));
        w.ConversationAwarenessChosen += on => _ = SendAap(a => a.SetConversationAwarenessAsync(on));
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
        sb.AppendLine($"Installed: {Installer.IsInstalled} (running installed copy: {Installer.IsRunningInstalled})  " +
                      $"notifications: {(Notifications.Enabled ? "on" : "off")}  last update check: {_cfg.LastUpdateCheck?.ToString("u") ?? "never"}");
        sb.AppendLine($"BLE running: {_ble.IsRunning}  warning: {_bleWarning ?? "none"}");
        sb.AppendLine($"Performance: {_perfSummary}");
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

        sb.AppendLine().AppendLine($"Advanced features: enabled={_cfg.AdvancedFeatures} driver installed={_driverInstalled} " +
                                   $"session={(_aap is null ? "none" : $"open, mode={_aap.NoiseMode}, CA={_aap.ConversationAwareness}")}");
        sb.AppendLine().AppendLine($"Lid: adverts every ~{_lidCadence.Threshold.TotalMilliseconds / 4:0} ms (closes card after {_lidCadence.Threshold.TotalMilliseconds:0} ms of silence)");
        sb.AppendLine().AppendLine("Ears, lid and ear-detection actions (newest last):");
        foreach (var line in _earLog) sb.AppendLine("  " + line);

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

    // ---------------- Calls: mic usage, read on change ----------------

    /// <summary>
    /// Feeds the call detector. The registry is only read when Windows reported a change (instant
    /// call detection), every 30 s as a safety net, or every tick if the watcher couldn't start.
    /// The detector itself still ticks every second for its end-of-call delay.
    /// </summary>
    private void CheckCalls()
    {
        var now = DateTimeOffset.Now;
        if (_micDirty || _micWatcher is null || now - _micReadAt > TimeSpan.FromSeconds(30))
        {
            _micDirty = false;
            _micReadAt = now;
            _micEntries = _micUsage.Read();
        }
        _calls.Tick(_micEntries, now);
    }

    private void UpdatePerformance(DateTimeOffset now)
    {
        var elapsed = now - _perfAt;
        if (elapsed < TimeSpan.FromMinutes(1)) return;
        _self.Refresh();
        var cpu = _self.TotalProcessorTime;
        var counts = _ble.Counts;
        double secs = elapsed.TotalSeconds;
        double cpuPct = (cpu - _perfCpu).TotalSeconds / secs / Environment.ProcessorCount * 100;
        long mine = _myAdverts - _perfMyAdverts;
        _perfSummary =
            $"CPU {cpuPct:0.00}% of the machine over the last {secs:0} s; " +
            $"memory: {_self.WorkingSet64 / (1024 * 1024)} MB working set, {_self.PrivateMemorySize64 / (1024 * 1024)} MB private, " +
            $"{GC.GetTotalMemory(false) / (1024 * 1024)} MB .NET heap; graphics tier {System.Windows.Media.RenderCapability.Tier >> 16} (2 = GPU, 0 = software)\n" +
            $"  Bluetooth filter {(_ble.AppleOnly ? "ON (Apple only)" : "OFF")}: " +
            $"{(counts.Delivered - _perfCounts.Delivered) / secs:0.0} adverts/s delivered, " +
            $"{(counts.Apple - _perfCounts.Apple) / secs:0.0}/s Apple, " +
            $"{(counts.Parsed - _perfCounts.Parsed) / secs:0.0}/s AirPods status\n" +
            $"  Your AirPods heard {(mine > 0 ? $"every ~{secs * 1000 / mine:0} ms" : "not at all")} in that minute\n" +
            $"  Mic check: {(_micWatcher is null ? "polling every second" : "on change (+ every 30 s)")}\n" +
            // In a compressed single-file exe every loaded assembly is unpacked into private memory,
            // so this list tells how much of "private" is code rather than data.
            $"  Assemblies loaded: {string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Order())}";
        _perfAt = now;
        _perfCpu = cpu;
        _perfCounts = counts;
        _perfMyAdverts = _myAdverts;
    }

    // ---------------- Notifications, install, updates ----------------

    /// <summary>Windows notification; falls back to PodBridge's own pop-up if notifications are off.</summary>
    private void Notify(string tag, string title, string body, HudKind kind = HudKind.Info,
        params (string Label, string Action)[] buttons)
    {
        if (!Notifications.Show(tag, title, body, buttons)) Hud.Show(title, body, kind, 6);
    }

    private void OnNotificationClicked(string action, Microsoft.Toolkit.Uwp.Notifications.ToastArguments args)
    {
        Log.Write($"Notification: {action}");
        switch (action)
        {
            case "move": Connect(); break;
            case "update": _ = InstallUpdateAsync(); break;
            case "dismiss": break;
            default: OpenSettings(); break;
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        _updateStatus = manual ? "Checking…" : _updateStatus;
        RefreshUi();
        try
        {
            var latest = await Updater.CheckAsync();
            _cfg.LastUpdateCheck = DateTimeOffset.Now;
            ConfigStore.Save(_cfg);
            if (latest is not null && latest.IsNewerThan(Updater.CurrentVersion))
            {
                bool isNew = _availableUpdate?.Version != latest.Version;
                _availableUpdate = latest;
                _updateStatus = $"PodBridge {latest.Version.ToString(3)} is available.";
                Log.Write($"Update available: {latest.Tag}");
                if (isNew || manual)
                    Notify("update", $"PodBridge {latest.Version.ToString(3)} is available",
                        Installer.IsRunningInstalled ? "Update now? PodBridge restarts in a few seconds."
                                                     : "Open the download page?",
                        HudKind.Info, ("Update", "update"), ("Later", "dismiss"));
            }
            else
            {
                _availableUpdate = null;
                _updateStatus = $"PodBridge is up to date ({Updater.CurrentVersion.ToString(3)}).";
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Update check failed: {ex.Message}");
            _updateStatus = "Couldn't check for updates. Check your connection and try again.";
        }
        finally
        {
            _updateBusy = false;
            RefreshUi();
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is not ReleaseInfo r || _updateBusy) return;
        if (!Installer.IsRunningInstalled || !r.HasDownload)
        {
            // Not installed (e.g. run from Downloads): there's nothing to swap in place.
            OpenUri(r.PageUrl);
            return;
        }
        _updateBusy = true;
        _updateStatus = $"Downloading PodBridge {r.Version.ToString(3)}…";
        RefreshUi();
        try
        {
            var exe = await Updater.DownloadAsync(r);
            _updateStatus = "Restarting…";
            RefreshUi();
            Updater.StartApply(exe);
            Quit();
        }
        catch (Exception ex)
        {
            Log.Write($"Update failed: {ex.Message}");
            _updateStatus = $"Update failed: {ex.Message}";
            _updateBusy = false;
            RefreshUi();
        }
    }

    private void InstallForUser()
    {
        try
        {
            var exe = Installer.Install();
            if (Installer.IsRunningInstalled) { RefreshUi(); return; }
            // Hand over to the installed copy; it waits for this one to exit.
            Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--wait-for-previous" } });
            Quit();
        }
        catch (Exception ex)
        {
            Log.Write($"Install failed: {ex}");
            Hud.Show("Couldn't install PodBridge", ex.Message, HudKind.Warning, 6);
        }
    }

    private static void StartUninstall()
    {
        var exe = Installer.IsInstalled ? Installer.InstalledExe : Environment.ProcessPath!;
        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--uninstall" } });
    }

    private static void OpenUri(string uri) =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    private void Quit()
    {
        _timer.Stop();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _timer.Stop();
        _lidTimer.Stop();
        StopAap();
        _ble.Dispose();
        _micWatcher?.Dispose();
        _tray.Dispose();
    }
}
