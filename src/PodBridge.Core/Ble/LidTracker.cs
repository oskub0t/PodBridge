namespace PodBridge.Core.Ble;

public enum LidEvent { None, Opened, Closed }

/// <summary>
/// Lid state from the advertisement's lid byte. Field captures (AirPods Pro 3):
///   bit 0x08 set   = lid closed (0x33 open → 0x3B closed → 0x34 open → 0x3C closed)
///   bit 0x20 set   = the advertising pod is IN the case. A pod that's out (in an ear, in a hand)
///                    sends 0x13 / 0x11, which says nothing about the lid, so it's ignored.
///   low bits       = count of lid openings
/// The AirPods keep advertising for a few seconds after closing, so this is far faster than
/// waiting for the adverts to stop.
/// </summary>
public sealed class LidTracker
{
    private const byte ClosedBit = 0x08;
    private const byte InCaseBit = 0x20;
    private bool? _open;

    public static bool IsOpen(AirPodsStatus s) => (s.RawLid & ClosedBit) == 0;

    /// <summary>Only a pod sitting in the case knows whether the lid is open.</summary>
    public static bool FromPodInCase(AirPodsStatus s) => (s.RawLid & InCaseBit) != 0;

    public bool? Open => _open;

    /// <returns>Opened/Closed on a change; None for the first reading, repeats, and adverts from a
    /// pod that's out of the case.</returns>
    public LidEvent Observe(AirPodsStatus s)
    {
        if (!FromPodInCase(s)) return LidEvent.None;
        bool open = IsOpen(s);
        bool? before = _open;
        _open = open;
        if (before is null || before == open) return LidEvent.None;
        return open ? LidEvent.Opened : LidEvent.Closed;
    }

    /// <summary>Forget the state (the AirPods were away; the next reading is a fresh start).</summary>
    public void Reset() => _open = null;
}
