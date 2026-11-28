namespace PodBridge.Core.Ble;

public enum LidEvent { None, Opened, Closed }

/// <summary>
/// Lid state from the advertisement's lid byte. Field captures (AirPods Pro 3):
///   bit 0x08 set   = lid closed
///   bit 0x20 set   = the advertising pod is IN the case (a pod that's out sends 0x1x: ignored)
///   bits 0x07      = a counter that changes with each opening (0x31 → 0x32 → … → 0x37 → 0x30)
///
/// Both pods report the lid, alternately, and one can lag behind the other by a second or more.
/// The two pods' counters are NOT reliably in sync: in the 0.4.6 field log the left pod said
/// opening 1 while the right said 2–4, and the left pod's counter went 3 → 1 after 12 s of
/// the case lying closed. Ordering reports across pods by counter (0.4.5) therefore dropped real
/// openings for half a minute. Rules now:
///  - Each pod is followed on its own (its reports arrive in order); only a CHANGE in a pod's
///    own report (open/closed or counter) is news.
///  - Opened: any pod changing to "open" while the lid is closed, unless it is a lagging pod
///    repeating the opening that was just closed (same counter, within <see cref="LagGrace"/>).
///    Missing a card is worse than a brief extra one.
///  - Closed: a pod changing to "closed" while the lid is open counts if that pod has reported
///    this opening as open, or carries the opener's counter, or the opening is older than
///    <see cref="LagGrace"/>. So a late "closed" from the previous opening can't dismiss the card
///    the new opening just showed (the 0.4.4 bug).
/// </summary>
public sealed class LidTracker
{
    private const byte ClosedBit = 0x08;
    private const byte InCaseBit = 0x20;
    private const byte CounterMask = 0x07;
    private const byte LeftPodBit = 0x20; // status byte: the advertiser is the left pod

    /// <summary>How long the other pod may lag behind before its reports are trusted anyway.</summary>
    public static readonly TimeSpan LagGrace = TimeSpan.FromSeconds(3);

    private readonly int?[] _podReport = new int?[2]; // last lid byte (state + counter) per pod
    private readonly bool[] _podSawCurrent = new bool[2]; // pod has reported the current state since it began
    private bool? _open;
    private int _counter;              // counter of the report that set the current state
    private DateTimeOffset _since;     // when the current state began

    public static bool IsOpen(AirPodsStatus s) => (s.RawLid & ClosedBit) == 0;

    /// <summary>Only a pod sitting in the case knows whether the lid is open.</summary>
    public static bool FromPodInCase(AirPodsStatus s) => (s.RawLid & InCaseBit) != 0;

    public bool? Open => _open;

    /// <summary>Why the last contrary report was ignored, for the diagnostics timeline; null if it wasn't.</summary>
    public string? LastIgnored { get; private set; }

    /// <returns>Opened/Closed on a real change; None for the first reading, repeats, lagging reports
    /// from the other pod, and adverts from a pod that's out of the case.</returns>
    public LidEvent Observe(AirPodsStatus s)
    {
        LastIgnored = null;
        if (!FromPodInCase(s)) return LidEvent.None;
        int pod = (s.RawStatus & LeftPodBit) != 0 ? 1 : 0;
        int report = s.RawLid & (ClosedBit | CounterMask);
        int counter = s.RawLid & CounterMask;
        bool open = IsOpen(s);
        bool changed = _podReport[pod] != report;
        _podReport[pod] = report;

        if (_open is not bool current)
        {
            SetState(open, counter, pod, s.Timestamp);
            return LidEvent.None;
        }
        if (open == current) { _podSawCurrent[pod] = true; return LidEvent.None; }
        if (!changed) return LidEvent.None; // this pod is still repeating an older state

        bool withinGrace = s.Timestamp - _since < LagGrace;
        if (open)
        {
            if (withinGrace && !_podSawCurrent[pod] && counter == _counter)
            {
                LastIgnored = $"late 'open' from the {PodName(pod)} pod for the opening that just closed";
                return LidEvent.None;
            }
            SetState(true, counter, pod, s.Timestamp);
            return LidEvent.Opened;
        }
        if (withinGrace && !_podSawCurrent[pod] && counter != _counter)
        {
            LastIgnored = $"late 'closed' from the {PodName(pod)} pod (it hasn't seen this opening yet)";
            return LidEvent.None;
        }
        SetState(false, counter, pod, s.Timestamp);
        return LidEvent.Closed;
    }

    private void SetState(bool open, int counter, int pod, DateTimeOffset at)
    {
        _open = open;
        _counter = counter;
        _since = at;
        _podSawCurrent[0] = _podSawCurrent[1] = false;
        _podSawCurrent[pod] = true;
    }

    private static string PodName(int pod) => pod == 1 ? "left" : "right";

    /// <summary>Forget the state (the AirPods were away; the next reading is a fresh start).</summary>
    public void Reset()
    {
        _open = null;
        _podReport[0] = _podReport[1] = null;
        LastIgnored = null;
    }
}
