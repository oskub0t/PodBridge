using PodBridge.Core.Ble;

namespace PodBridge.Core.Features;

public sealed record LowBatteryAlert(int Threshold, int Percent, bool Left, bool Right);

/// <summary>
/// Low-battery alerts at 20 % and 10 %, like iOS. Each threshold fires once per discharge; charging
/// (or climbing back above 30 %) re-arms it. Left and right crossing together give one alert.
/// The case is not alerted on (iOS doesn't either).
/// </summary>
public sealed class LowBatteryMonitor
{
    private static readonly int[] Thresholds = [20, 10];
    private const int RearmAbove = 30;
    private int? _leftNotified, _rightNotified;

    public LowBatteryAlert? Observe(AirPodsStatus s)
    {
        int? l = Check(s.LeftPercent, s.LeftCharging, ref _leftNotified);
        int? r = Check(s.RightPercent, s.RightCharging, ref _rightNotified);
        if (l is null && r is null) return null;

        int threshold = Math.Min(l ?? int.MaxValue, r ?? int.MaxValue);
        int pct = Math.Min(l is not null ? s.LeftPercent!.Value : int.MaxValue,
                           r is not null ? s.RightPercent!.Value : int.MaxValue);
        return new LowBatteryAlert(threshold, pct, l is not null, r is not null);
    }

    private static int? Check(int? pct, bool charging, ref int? notified)
    {
        if (pct is not int p) return null;
        if (charging || p > RearmAbove) { notified = null; return null; }

        int? hit = null;
        foreach (var t in Thresholds) if (p <= t) hit = t; // lowest threshold reached
        if (hit is null || (notified is int n && hit >= n)) return null;
        notified = hit;
        return hit;
    }
}
