namespace PodBridge.Core.Calls;

/// <summary>One entry from Windows' per-app microphone usage record.</summary>
public sealed record MicUsageEntry(string AppKey, bool InUse);

public interface IMicUsageSource
{
    IReadOnlyList<MicUsageEntry> Read();
}

/// <summary>
/// "In a call" = a configured call app currently holds the microphone. Deliberately NOT "Teams is
/// running": Teams runs all day, and switching to headset mode whenever it is open would leave music
/// in low-quality mono permanently.
///
/// Debounced: Teams briefly releases the mic when devices change (including when we change them),
/// so a call only ends after the mic has been free for <see cref="EndDelay"/>.
/// </summary>
public sealed class CallDetector
{
    private readonly Func<PodBridgeConfig> _config;
    private DateTimeOffset? _freeSince;

    public CallDetector(Func<PodBridgeConfig> config) => _config = config;

    public TimeSpan EndDelay { get; init; } = TimeSpan.FromSeconds(6);
    public bool InCall { get; private set; }
    public string? CallApp { get; private set; }

    public event Action<string>? CallStarted;
    public event Action? CallEnded;

    public void Tick(IReadOnlyList<MicUsageEntry> entries, DateTimeOffset now)
    {
        var apps = _config().CallApps;
        var active = entries.FirstOrDefault(e =>
            e.InUse && apps.Any(a => e.AppKey.Contains(a, StringComparison.OrdinalIgnoreCase)));

        if (active is not null)
        {
            _freeSince = null;
            if (!InCall)
            {
                InCall = true;
                CallApp = active.AppKey;
                CallStarted?.Invoke(active.AppKey);
            }
            return;
        }

        if (!InCall) return;
        _freeSince ??= now;
        if (now - _freeSince >= EndDelay)
        {
            InCall = false;
            CallApp = null;
            _freeSince = null;
            CallEnded?.Invoke();
        }
    }
}
