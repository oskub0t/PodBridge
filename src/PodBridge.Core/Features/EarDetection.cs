namespace PodBridge.Core.Features;

public enum EarAction { None, Pause, Resume }

/// <summary>
/// Automatic ear detection from the in-ear flags in the BLE advertisement.
/// Take an AirPod out → pause. Put it back within <see cref="ResumeWindow"/> → resume.
///
/// Works on the *number* of pods in ear (0-2), so it doesn't depend on the left/right mapping.
/// A new count must be seen again after <see cref="Settle"/> before it counts, which filters
/// one-off glitches. On Apple devices this is instant (it uses the private link); here it waits for
/// the next advertisement, typically one or two seconds.
/// </summary>
public sealed class EarDetection
{
    private int? _stable;
    private int? _candidate;
    private DateTimeOffset _candidateSince;
    private int? _pausedAtLevel;
    private DateTimeOffset _pausedAt;

    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(600);
    public TimeSpan ResumeWindow { get; init; } = TimeSpan.FromMinutes(10);

    public int? InEarCount => _stable;

    public EarAction Observe(int inEarCount, DateTimeOffset now)
    {
        if (_stable is null) { _stable = inEarCount; return EarAction.None; }
        if (inEarCount == _stable) { _candidate = null; return EarAction.None; }

        if (_candidate != inEarCount)
        {
            _candidate = inEarCount;
            _candidateSince = now;
            return EarAction.None;
        }
        if (now - _candidateSince < Settle) return EarAction.None;

        int previous = _stable.Value;
        _stable = inEarCount;
        _candidate = null;

        if (inEarCount < previous)
        {
            _pausedAtLevel = previous;
            _pausedAt = now;
            return EarAction.Pause;
        }
        if (_pausedAtLevel is int level && inEarCount >= level && now - _pausedAt <= ResumeWindow)
        {
            _pausedAtLevel = null;
            return EarAction.Resume;
        }
        return EarAction.None;
    }

    /// <summary>Forget state (e.g. when the AirPods disconnect from this PC).</summary>
    public void Reset()
    {
        _stable = _candidate = _pausedAtLevel = null;
    }
}
