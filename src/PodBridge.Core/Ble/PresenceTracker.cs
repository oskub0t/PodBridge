namespace PodBridge.Core.Ble;

/// <summary>
/// Decides which advertisements are "mine" and turns a stream of adverts into a "case just opened"
/// event. AirPods advertise whenever the lid is open or they are in use, and every PC in range hears
/// it, so without a filter your colleague's case would pop up on your screen.
///
/// Filter: model ID (learned) + minimum signal strength. Different models (your case) separate
/// cleanly. Two identical models side by side can only be told apart by RSSI, because the rest of
/// the payload is encrypted with a key Windows doesn't have.
/// </summary>
public sealed class PresenceTracker
{
    private readonly Func<PodBridgeConfig> _config;
    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;

    public PresenceTracker(Func<PodBridgeConfig> config) => _config = config;

    public AirPodsStatus? Latest { get; private set; }

    /// <summary>Raised when "my" AirPods are seen after being absent (≈ the case was opened).</summary>
    public event Action<AirPodsStatus>? Appeared;

    /// <summary>Raised for every accepted advert.</summary>
    public event Action<AirPodsStatus>? Updated;

    /// <summary>Raised for adverts that look like AirPods but fail the filter (for learn mode).</summary>
    public event Action<AirPodsStatus>? Candidate;

    public bool IsMine(AirPodsStatus s)
    {
        var c = _config();
        if (s.Rssi < c.MinRssi) return false;
        return c.MyModelId is ushort id && s.ModelId == id;
    }

    public void OnAdvertisement(AirPodsStatus s)
    {
        if (!IsMine(s))
        {
            if (s.Rssi >= _config().MinRssi) Candidate?.Invoke(s);
            return;
        }

        bool wasAbsent = s.Timestamp - _lastSeen > TimeSpan.FromSeconds(_config().AbsenceSeconds);
        _lastSeen = s.Timestamp;
        Latest = s;
        if (wasAbsent) Appeared?.Invoke(s);
        Updated?.Invoke(s);
    }

    public bool IsStale(DateTimeOffset now) =>
        Latest is null || now - _lastSeen > TimeSpan.FromSeconds(_config().AbsenceSeconds);
}
