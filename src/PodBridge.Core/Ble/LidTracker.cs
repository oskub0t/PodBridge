namespace PodBridge.Core.Ble;

public enum LidEvent { None, Opened, Closed }

/// <summary>
/// Lid state from the advertisement's lid byte. Field captures (AirPods Pro 3): the low bits count
/// lid openings and bit 0x08 is set while the lid is closed (0x33 open → 0x3B closed → 0x34 open
/// → 0x3C closed). The AirPods keep advertising for a few seconds after closing, so this is far
/// faster than waiting for the adverts to stop.
/// </summary>
public sealed class LidTracker
{
    private const byte ClosedBit = 0x08;
    private bool? _open;

    public static bool IsOpen(AirPodsStatus s) => (s.RawLid & ClosedBit) == 0;

    public bool? Open => _open;

    /// <returns>Opened/Closed on a change; None for the first reading and for repeats.</returns>
    public LidEvent Observe(AirPodsStatus s)
    {
        bool open = IsOpen(s);
        bool? before = _open;
        _open = open;
        if (before is null || before == open) return LidEvent.None;
        return open ? LidEvent.Opened : LidEvent.Closed;
    }

    /// <summary>Forget the state (the AirPods were away; the next reading is a fresh start).</summary>
    public void Reset() => _open = null;
}
