using Windows.Media.Control;

namespace PodBridge.App.Platform;

/// <summary>
/// Windows' system media controls (the same sessions the volume flyout shows: Spotify, browsers,
/// Media Player...). Used to pause and resume for ear detection, and to notice "this PC started
/// playing". No admin; works in an unpackaged desktop app.
/// Events fire on a thread-pool thread.
/// </summary>
internal sealed class MediaSessions
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private readonly HashSet<string> _hooked = [];
    private readonly HashSet<string> _pausedByUs = [];
    private bool _anyPlaying;
    private readonly object _gate = new();

    public event Action? PlaybackStarted;

    public bool HasPausedByUs => _pausedByUs.Count > 0;

    public async Task InitAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.SessionsChanged += (_, _) => Hook();
            Hook();
        }
        catch (Exception ex) { Log.Write($"Media sessions unavailable: {ex.Message}"); }
    }

    private void Hook()
    {
        if (_mgr is null) return;
        lock (_gate)
        {
            foreach (var s in _mgr.GetSessions())
                if (_hooked.Add(Key(s))) s.PlaybackInfoChanged += (_, _) => Evaluate();
        }
        Evaluate();
    }

    private void Evaluate()
    {
        if (_mgr is null) return;
        bool playing = _mgr.GetSessions().Any(IsPlaying);
        bool started;
        lock (_gate)
        {
            started = playing && !_anyPlaying;
            _anyPlaying = playing;
        }
        if (started) PlaybackStarted?.Invoke();
    }

    public async Task<int> PauseAllAsync()
    {
        if (_mgr is null) return 0;
        int n = 0;
        foreach (var s in _mgr.GetSessions().Where(IsPlaying))
        {
            if (await s.TryPauseAsync()) { _pausedByUs.Add(Key(s)); n++; }
        }
        Log.Write($"Ear detection: paused {n} session(s)");
        return n;
    }

    /// <summary>Resumes only what we paused, so taking an AirPod out never starts something new.</summary>
    public async Task<int> ResumeAsync()
    {
        if (_mgr is null || _pausedByUs.Count == 0) return 0;
        int n = 0;
        foreach (var s in _mgr.GetSessions().Where(s => _pausedByUs.Contains(Key(s)) && !IsPlaying(s)))
            if (await s.TryPlayAsync()) n++;
        _pausedByUs.Clear();
        Log.Write($"Ear detection: resumed {n} session(s)");
        return n;
    }

    public void ForgetPaused() => _pausedByUs.Clear();

    private static string Key(GlobalSystemMediaTransportControlsSession s) => s.SourceAppUserModelId;

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s) =>
        s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
}
