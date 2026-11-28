namespace PodBridge.Core.Ble;

public enum LidEvent { None, Opened, Closed }

/// <summary>
/// Lid state from the advertisement's lid byte. Field captures (AirPods Pro 3):
///   bit 0x08 set   = lid closed
///   bit 0x20 set   = the advertising pod is IN the case (a pod that's out sends 0x1x: ignored)
///   bits 0x07      = counter of lid openings (0x31 → 0x32 → … → 0x37 → 0x30)
/// Both pods report it, and one is often a moment late. A late pod's report carries the OLDER
/// counter, so: a newer counter = a new opening; "closed" only counts for the current opening;
/// reports with an older counter are ignored. Without this, a late "closed" from opening #6
/// dismissed the card that opening #7 had just shown.
/// </summary>
public sealed class LidTracker
{
    private const byte ClosedBit = 0x08;
    private const byte InCaseBit = 0x20;
    private const byte CounterMask = 0x07;
    private int? _count;
    private bool _open;

    public static bool IsOpen(AirPodsStatus s) => (s.RawLid & ClosedBit) == 0;

    /// <summary>Only a pod sitting in the case knows whether the lid is open.</summary>
    public static bool FromPodInCase(AirPodsStatus s) => (s.RawLid & InCaseBit) != 0;

    public bool? Open => _count is null ? null : _open;

    /// <returns>Opened/Closed on a real change; None for the first reading, repeats, stale reports
    /// from the other pod, and adverts from a pod that's out of the case.</returns>
    public LidEvent Observe(AirPodsStatus s)
    {
        if (!FromPodInCase(s)) return LidEvent.None;
        int count = s.RawLid & CounterMask;
        bool open = IsOpen(s);
        if (_count is not int current)
        {
            _count = count;
            _open = open;
            return LidEvent.None;
        }

        int ahead = (count - current) & CounterMask;   // the counter wraps 7 → 0
        if (ahead == 0)
        {
            // Same opening: only "it closed" is news. A same-count "open" after closing is a late pod.
            if (!open && _open) { _open = false; return LidEvent.Closed; }
            return LidEvent.None;
        }
        if (ahead <= 3)
        {
            bool wasOpen = _open;
            _count = count;
            _open = open;
            return open ? LidEvent.Opened : (wasOpen ? LidEvent.Closed : LidEvent.None);
        }
        return LidEvent.None; // older counter: a late report from the other pod
    }

    /// <summary>Forget the state (the AirPods were away; the next reading is a fresh start).</summary>
    public void Reset() => _count = null;
}
