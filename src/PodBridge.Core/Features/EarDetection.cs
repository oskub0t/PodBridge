namespace PodBridge.Core.Features;

public enum EarAction { None, Pause, Resume }

/// <summary>
/// Automatic ear detection from the in-ear flags in the BLE advertisement.
/// Take an AirPod out → pause. Put it back within <see cref="ResumeWindow"/> → resume.
///
/// Works on the *number* of pods in ear (0-2). Acts on the first reading that differs: worn AirPods
/// only advertise about every 0.75 s, so waiting for a second confirming reading doubled the delay.
/// A one-off misread corrects itself (a brief pause, then resume, since resume only restarts what we
/// paused). <see cref="Settle"/> can re-enable confirmation if a model ever proves noisy.
/// </summary>
public sealed class EarDetection
{
    private int? _stable;
    private int? _candidate;
    private DateTimeOffset _candidateSince;
    private int? _pausedAtLevel;
    private DateTimeOffset _pausedAt;

    /// <summary>Zero (default): act on the first differing reading. Otherwise the new count must be
    /// seen again after this long.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.Zero;
    public TimeSpan ResumeWindow { get; init; } = TimeSpan.FromMinutes(10);

    public int? InEarCount => _stable;

    public EarAction Observe(int inEarCount, DateTimeOffset now)
    {
        if (_stable is null) { _stable = inEarCount; return EarAction.None; }
        if (inEarCount == _stable) { _candidate = null; return EarAction.None; }

        if (Settle > TimeSpan.Zero)
        {
            if (_candidate != inEarCount)
            {
                _candidate = inEarCount;
                _candidateSince = now;
                return EarAction.None;
            }
            if (now - _candidateSince < Settle) return EarAction.None;
        }
        return Commit(inEarCount, now);
    }

    /// <summary>
    /// For exact, one-off events (the advanced driver's in-ear messages): always immediate.
    /// </summary>
    public EarAction ObserveExact(int inEarCount, DateTimeOffset now)
    {
        if (_stable is null || inEarCount == _stable) { _stable = inEarCount; _candidate = null; return EarAction.None; }
        return Commit(inEarCount, now);
    }

    private EarAction Commit(int inEarCount, DateTimeOffset now)
    {
        int previous = _stable!.Value;
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
