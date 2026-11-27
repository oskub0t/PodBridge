using PodBridge.Core.Ble;

namespace PodBridge.Core.Features;

/// <summary>
/// Combines the two pods' advertisements into one in-ear count.
///
/// Both pods advertise, each from its own point of view (status bit 0x20 = "I am the left pod").
/// Field captures (AirPods Pro 3) show:
///   - a pod OUT of the case reports both pods reliably (0x02 = me in ear, 0x08 = other in ear);
///   - a pod IN the case (lid byte bit 0x20, or charging) uses those bits differently: with the left
///     pod in an ear and the right one charging, the right pod sent 0x53, which the out-of-case rule
///     reads as "right in, left out". Alternating with the left pod's correct 0x33, the count
///     flipped 1/0 several times a second.
/// So a pod in the case only tells us that IT isn't in an ear; it never speaks for the other pod.
/// </summary>
public sealed class EarStateMerger
{
    private bool? _left, _right;

    public int? Count => _left is null && _right is null ? null : (_left == true ? 1 : 0) + (_right == true ? 1 : 0);

    public int Observe(AirPodsStatus s)
    {
        bool advertiserIsLeft = (s.RawStatus & 0x20) != 0;
        bool advertiserCharging = advertiserIsLeft ? s.LeftCharging : s.RightCharging;
        bool advertiserInCase = LidTracker.FromPodInCase(s) || advertiserCharging;

        if (advertiserInCase)
        {
            if (advertiserIsLeft) _left = false; else _right = false;
        }
        else
        {
            _left = s.LeftInEar && !s.LeftCharging;
            _right = s.RightInEar && !s.RightCharging;
        }
        return Count ?? 0;
    }

    public void Reset() => _left = _right = null;
}
