namespace PodBridge.Core.Ble;

/// <summary>Last known reading of one part (left, right or case).</summary>
public sealed record PartReading(int Percent, bool Charging, DateTimeOffset At);

/// <summary>What to show for one part. <see cref="Age"/> is null when the value is live.</summary>
public sealed record PartView(int? Percent, bool Charging, TimeSpan? Age)
{
    public bool IsLive => Age is null;
    public static readonly PartView Unknown = new(null, false, null);
}

public sealed record BatteryView(PartView Left, PartView Right, PartView Case)
{
    public bool HasAny => Left.Percent is not null || Right.Percent is not null || Case.Percent is not null;

    public int? LowestPodPercent =>
        (Left.Percent, Right.Percent) switch
        {
            (int l, int r) => Math.Min(l, r),
            (int l, null) => l,
            (null, int r) => r,
            _ => null,
        };

    /// <summary>Oldest age among the shown parts, or null if everything shown is live.</summary>
    public TimeSpan? OldestAge =>
        new[] { Left, Right, Case }.Where(p => p.Percent is not null && p.Age is not null)
            .Select(p => p.Age!.Value).DefaultIfEmpty().Max() is var max && max > TimeSpan.Zero ? max : null;

    /// <summary>A live view straight from one advertisement (e.g. someone else's AirPods during setup).</summary>
    public static BatteryView Live(AirPodsStatus s) => new(
        new PartView(s.LeftPercent, s.LeftCharging, null),
        new PartView(s.RightPercent, s.RightCharging, null),
        new PartView(s.CasePercent, s.CaseCharging, null));

    /// <summary>"just now", "12 min ago", "3 h ago", "yesterday", "4 days ago".</summary>
    public static string FormatAge(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalMinutes: < 60 } => $"{(int)age.TotalMinutes} min ago",
        { TotalHours: < 24 } => $"{(int)age.TotalHours} h ago",
        { TotalDays: < 2 } => "yesterday",
        _ => $"{(int)age.TotalDays} days ago",
    };
}

/// <summary>
/// Remembers the last known level of each part separately, like the iOS Batteries widget keeps
/// showing the case after its lid closes. Adverts often report a part as unknown (case shut, pod
/// out of range); that must not erase the last real value. Old values are marked with their age
/// and never show "charging", since a stale charging flag is exactly what goes wrong.
/// Serialisable (public properties) so it survives restarts.
/// </summary>
public sealed class BatteryMemory
{
    public ushort? ModelId { get; set; }
    public PartReading? Left { get; set; }
    public PartReading? Right { get; set; }
    public PartReading? Case { get; set; }

    /// <summary>Readings newer than this count as live.</summary>
    public static TimeSpan LiveWindow { get; } = TimeSpan.FromSeconds(30);

    /// <returns>True if any remembered percentage or charging state changed (worth saving).</returns>
    public bool Observe(AirPodsStatus s)
    {
        if (ModelId != s.ModelId)
        {
            ModelId = s.ModelId;
            Left = Right = Case = null;
        }
        bool changed = false;
        Left = Merge(Left, s.LeftPercent, s.LeftCharging, s.Timestamp, ref changed);
        Right = Merge(Right, s.RightPercent, s.RightCharging, s.Timestamp, ref changed);
        Case = Merge(Case, s.CasePercent, s.CaseCharging, s.Timestamp, ref changed);
        return changed;
    }

    private static PartReading? Merge(PartReading? old, int? pct, bool charging, DateTimeOffset at, ref bool changed)
    {
        if (pct is not int p) return old;
        if (old is null || old.Percent != p || old.Charging != charging) changed = true;
        return new PartReading(p, charging, at);
    }

    public BatteryView View(DateTimeOffset now) => new(Part(Left, now), Part(Right, now), Part(Case, now));

    private static PartView Part(PartReading? r, DateTimeOffset now)
    {
        if (r is null) return PartView.Unknown;
        var age = now - r.At;
        return age <= LiveWindow
            ? new PartView(r.Percent, r.Charging, null)
            : new PartView(r.Percent, false, age < TimeSpan.Zero ? TimeSpan.Zero : age);
    }

    public void Clear()
    {
        ModelId = null;
        Left = Right = Case = null;
    }
}
