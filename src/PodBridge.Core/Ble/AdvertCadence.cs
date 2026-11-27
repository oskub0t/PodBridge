namespace PodBridge.Core.Ble;

/// <summary>
/// Learns how often one pair of AirPods advertises and says when they've gone quiet: closing the
/// lid stops the advertisements. The threshold is a few typical gaps (median × <see cref="Factor"/>),
/// clamped between <see cref="Min"/> and <see cref="Max"/>. That's fast when they advertise often,
/// and still robust when Windows delivers them in bursts.
/// </summary>
public sealed class AdvertCadence
{
    private readonly Queue<TimeSpan> _gaps = new();
    private DateTimeOffset? _last;

    public TimeSpan Min { get; init; } = TimeSpan.FromMilliseconds(350);
    public TimeSpan Max { get; init; } = TimeSpan.FromSeconds(2.5);
    public double Factor { get; init; } = 4;

    public void Observe(DateTimeOffset at)
    {
        if (_last is DateTimeOffset prev)
        {
            var gap = at - prev;
            // Ignore same-instant duplicates and long pauses (those aren't the normal rhythm).
            if (gap >= TimeSpan.FromMilliseconds(5) && gap < Max)
            {
                _gaps.Enqueue(gap);
                while (_gaps.Count > 12) _gaps.Dequeue();
            }
        }
        _last = at;
    }

    public TimeSpan Threshold
    {
        get
        {
            if (_gaps.Count < 3) return Max; // not enough data yet: be conservative
            var sorted = _gaps.OrderBy(g => g).ToList();
            var median = sorted[sorted.Count / 2];
            var t = TimeSpan.FromTicks((long)(median.Ticks * Factor));
            return t < Min ? Min : t > Max ? Max : t;
        }
    }

    public bool LooksGone(DateTimeOffset now) => _last is DateTimeOffset l && now - l > Threshold;

    public void Reset()
    {
        _gaps.Clear();
        _last = null;
    }
}
