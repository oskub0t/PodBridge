namespace PodBridge.Core.Features;

/// <summary>When PodBridge connects the AirPods by itself.</summary>
public enum ConnectTrigger
{
    /// <summary>When a bud goes in your ear (default; like Apple, nothing happens while they sit in the case).</summary>
    PutInEar,
    /// <summary>As soon as the case opens (fastest, but connects AirPods that are still in the case).</summary>
    CaseOpens,
}

/// <summary>Decides when to ask Windows to connect, with a cooldown so a failing connect isn't retried every advert.</summary>
public sealed class AutoConnectPolicy
{
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(15);

    public bool OnCaseOpened(PodBridgeConfig c, bool connected, bool userRecentlyDisconnected, DateTimeOffset now) =>
        Decide(c.AutoConnect && c.ConnectWhen == ConnectTrigger.CaseOpens, connected, userRecentlyDisconnected, now);

    public bool OnInEarCount(PodBridgeConfig c, int inEar, bool connected, bool userRecentlyDisconnected, DateTimeOffset now) =>
        Decide(c.AutoConnect && c.ConnectWhen == ConnectTrigger.PutInEar && inEar > 0, connected, userRecentlyDisconnected, now);

    private bool Decide(bool wanted, bool connected, bool recentlyDisconnected, DateTimeOffset now)
    {
        if (!wanted || connected || recentlyDisconnected || now - _lastAttempt < Cooldown) return false;
        _lastAttempt = now;
        return true;
    }
}
