namespace PodBridge.Core.Audio;

/// <summary>
/// Owns the Windows default-device switching.
///
/// Music mode: default output = stereo "Headphones" endpoint; default *communications* output/mic =
/// AirPods hands-free; the default (console) mic is left alone. If a random app opens the default
/// mic, Bluetooth drops to mono hands-free, so we don't make the AirPods mic the everyday default.
///
/// Call mode: every role points at the hands-free output + AirPods mic, so apps set to "Default"
/// follow. The previous console defaults are saved and restored after the call.
///
/// Limit: if Teams has a *specific* device picked in its own settings, Windows defaults don't
/// override it. Set Teams to "Default device" / "Communications device"; the silent-mic warning
/// catches the case where it isn't.
/// </summary>
public sealed class AudioModeController
{
    private readonly IAudioBackend _backend;
    private readonly Func<PodBridgeConfig> _config;
    private readonly Action<string> _log;
    private string? _savedConsoleRender, _savedMultimediaRender, _savedConsoleCapture;
    private bool _inCallMode;

    public AudioModeController(IAudioBackend backend, Func<PodBridgeConfig> config, Action<string> log)
    {
        _backend = backend;
        _config = config;
        _log = log;
    }

    public bool InCallMode => _inCallMode;

    public ResolvedEndpoints? Resolve() => EndpointResolver.Resolve(_backend.GetEndpoints(), _config());

    /// <returns>False if the AirPods aren't connected (nothing to switch to).</returns>
    public bool ApplyMusicMode()
    {
        var r = Resolve();
        if (r is null || !r.AnyActive) { _log("Music mode: AirPods not connected."); return false; }

        var music = Active(r.MusicOutput) ?? Active(r.CallOutput);
        Set(music, Role.Console);
        Set(music, Role.Multimedia);
        Set(Active(r.CallOutputOrMusic), Role.Communications);
        Set(Active(r.Mic), Role.Communications);

        // The everyday default mic must not be the AirPods: any app that opens it (voice typing,
        // a browser tab, a meeting app idling) drops the AirPods to mono hands-free audio.
        if (_config().LaptopMicOutsideCalls && r.Mic is { } airMic)
        {
            var fallback = PickNonAirPodsMic(airMic.Id);
            foreach (var role in new[] { Role.Console, Role.Multimedia })
                if (fallback is not null && _backend.GetDefaultId(Flow.Capture, role) == airMic.Id)
                    _backend.SetDefault(fallback.Id, role);
        }
        else if (_inCallMode && _savedConsoleCapture is not null && _savedConsoleCapture != r.Mic?.Id)
        {
            _backend.SetDefault(_savedConsoleCapture, Role.Console);
        }

        _inCallMode = false;
        _savedConsoleRender = _savedMultimediaRender = _savedConsoleCapture = null;
        _log($"Music mode → {music?.Name ?? "?"}");
        return true;
    }

    public bool ApplyCallMode()
    {
        var r = Resolve();
        if (r is null || !r.AnyActive) { _log("Call mode: AirPods not connected."); return false; }

        if (!_inCallMode)
        {
            _savedConsoleRender = _backend.GetDefaultId(Flow.Render, Role.Console);
            _savedMultimediaRender = _backend.GetDefaultId(Flow.Render, Role.Multimedia);
            _savedConsoleCapture = _backend.GetDefaultId(Flow.Capture, Role.Console);
        }

        var output = Active(r.CallOutputOrMusic);
        var mic = Active(r.Mic);
        foreach (var role in Enum.GetValues<Role>())
        {
            Set(output, role);
            Set(mic, role);
        }
        _inCallMode = true;
        _log($"Call mode → out: {output?.Name ?? "?"}, mic: {mic?.Name ?? "none found"}");
        return true;
    }

    /// <summary>The mic that was default before the call, else the first other active mic
    /// (prefers built-in "Microphone" entries over e.g. a dock or display).</summary>
    private AudioEndpoint? PickNonAirPodsMic(string airPodsMicId)
    {
        var mics = _backend.GetEndpoints()
            .Where(e => e.Flow == Flow.Capture && e.Active && e.Id != airPodsMicId)
            .ToList();
        return mics.FirstOrDefault(e => e.Id == _savedConsoleCapture)
               ?? mics.FirstOrDefault(e => e.Name.Contains("Microphone", StringComparison.OrdinalIgnoreCase))
               ?? mics.FirstOrDefault();
    }

    private static AudioEndpoint? Active(AudioEndpoint? e) => e is { Active: true } ? e : null;

    private void Set(AudioEndpoint? e, Role role)
    {
        if (e is null) return;
        if (_backend.GetDefaultId(e.Flow, role) == e.Id) return;
        _backend.SetDefault(e.Id, role);
    }
}
