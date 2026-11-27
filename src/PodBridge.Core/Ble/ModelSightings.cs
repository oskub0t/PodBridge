namespace PodBridge.Core.Ble;

/// <summary>
/// A model ID only counts as real once it has been seen several times in a short window. Genuine
/// IDs repeat in every advertisement. If a newer AirPods format puts encrypted bytes where the ID
/// used to be, the value changes every time and never qualifies, so setup can't save a value that
/// never shows up again.
/// </summary>
public sealed class ModelSightings
{
    private readonly Dictionary<ushort, (int Count, DateTimeOffset First)> _seen = [];

    public int Required { get; init; } = 3;
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(20);

    /// <returns>True when this model has now been seen <see cref="Required"/> times within the window.</returns>
    public bool Observe(AirPodsStatus s)
    {
        if (!_seen.TryGetValue(s.ModelId, out var e) || s.Timestamp - e.First > Window)
            e = (0, s.Timestamp);
        e.Count++;
        _seen[s.ModelId] = e;
        if (_seen.Count > 256) _seen.Clear(); // random IDs must not grow this forever
        return e.Count >= Required;
    }

    public void Clear() => _seen.Clear();
}
