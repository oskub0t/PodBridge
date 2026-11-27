using PodBridge.Core.Ble;

namespace PodBridge.Core.Features;

/// <summary>Mirrors Apple's "Connect to This Mac: Automatically / When Last Connected".</summary>
public enum SwitchPolicy { Never, Ask, Automatically }

public enum SwitchDecision { None, Offer, Connect }

/// <summary>
/// "Move to this PC": you're wearing your AirPods (connected to your phone, say) and this PC starts
/// playing audio or a call. Apple does this through iCloud; we infer "worn" from the in-ear flags
/// in a recent advertisement.
/// </summary>
public sealed class SwitchAdvisor
{
    private DateTimeOffset _lastOffer = DateTimeOffset.MinValue;

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan Freshness { get; init; } = TimeSpan.FromSeconds(20);

    public SwitchDecision OnPcAudioStarted(
        SwitchPolicy policy, AirPodsStatus? latest, bool connectedToPc, bool userRecentlyDisconnected,
        DateTimeOffset now)
    {
        if (policy == SwitchPolicy.Never || connectedToPc || userRecentlyDisconnected) return SwitchDecision.None;
        if (latest is null || now - latest.Timestamp > Freshness) return SwitchDecision.None;
        if (!latest.LeftInEar && !latest.RightInEar) return SwitchDecision.None;
        if (now - _lastOffer < Cooldown) return SwitchDecision.None;

        _lastOffer = now;
        return policy == SwitchPolicy.Automatically ? SwitchDecision.Connect : SwitchDecision.Offer;
    }
}
