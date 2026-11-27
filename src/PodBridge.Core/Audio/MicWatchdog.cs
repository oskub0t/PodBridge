namespace PodBridge.Core.Audio;

/// <summary>
/// During a call, watches the AirPods mic's peak meter. Quiet speech or room noise still gives
/// small non-zero peaks, but a reading of exactly zero for a long time usually means *no app is
/// recording from the AirPods mic at all*: Teams is on the laptop mic, or the AirPods hopped to the
/// iPhone. That is the "my mic isn't picking up" case, so we warn once per call.
/// </summary>
public sealed class MicWatchdog
{
    private const float DigitalSilence = 1e-6f;
    private DateTimeOffset? _silentSince;
    private bool _warned;

    public void Reset()
    {
        _silentSince = null;
        _warned = false;
    }

    /// <returns>True exactly once when the warning should be shown.</returns>
    public bool Tick(float? peak, DateTimeOffset now, int thresholdSeconds)
    {
        if (thresholdSeconds <= 0 || _warned) return false;
        if (peak is null || peak > DigitalSilence)
        {
            _silentSince = null;
            return false;
        }
        _silentSince ??= now;
        if (now - _silentSince < TimeSpan.FromSeconds(thresholdSeconds)) return false;
        _warned = true;
        return true;
    }
}
