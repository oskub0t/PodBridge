namespace PodBridge.Core.Aap;

/// <summary>
/// Moves AAP packets to and from the AirPods. On Windows this is the optional PodBridge driver
/// (see docs/advanced-features.md); tests use a fake.
/// </summary>
public interface IAapTransport : IDisposable
{
    Task SendAsync(byte[] packet);

    /// <summary>One complete AAP packet per event. May fire on any thread.</summary>
    event Action<byte[]>? Received;

    /// <summary>The AirPods went away or the driver closed the channel.</summary>
    event Action? Closed;
}

/// <summary>
/// One control session with one pair of AirPods: opens it, keeps the latest state, and exposes
/// commands. Holds no UI or Windows code, so it is fully testable.
/// </summary>
public sealed class AapSession : IDisposable
{
    private readonly IAapTransport _transport;

    public AapSession(IAapTransport transport)
    {
        _transport = transport;
        _transport.Received += OnPacket;
        _transport.Closed += () => Closed?.Invoke();
    }

    public NoiseMode? NoiseMode { get; private set; }
    public bool? ConversationAwareness { get; private set; }
    public AapBattery? Battery { get; private set; }
    public int? InEarCount { get; private set; }

    public event Action<NoiseMode>? NoiseModeChanged;
    public event Action<bool>? ConversationAwarenessChanged;
    public event Action<AapBattery>? BatteryChanged;
    public event Action<int>? InEarCountChanged;
    public event Action<byte>? SpeechLevelChanged;
    public event Action? Closed;

    /// <summary>Handshake, announce features, subscribe to updates, in that order.</summary>
    public async Task StartAsync()
    {
        await _transport.SendAsync(AapCodec.Handshake);
        await _transport.SendAsync(AapCodec.SetFeatureFlags);
        await _transport.SendAsync(AapCodec.RequestNotifications);
    }

    public Task SetNoiseModeAsync(NoiseMode mode) => _transport.SendAsync(AapCodec.SetNoiseMode(mode));

    public Task SetConversationAwarenessAsync(bool on) => _transport.SendAsync(AapCodec.SetConversationAwareness(on));

    private void OnPacket(byte[] packet)
    {
        switch (AapCodec.Parse(packet))
        {
            case AapBattery b:
                Battery = b;
                BatteryChanged?.Invoke(b);
                break;
            case AapEarState e when e.InEarCount != InEarCount:
                InEarCount = e.InEarCount;
                InEarCountChanged?.Invoke(e.InEarCount);
                break;
            case AapControl c when c.AsNoiseMode is NoiseMode m:
                NoiseMode = m;
                NoiseModeChanged?.Invoke(m);
                break;
            case AapControl c when c.AsConversationAwareness is bool ca:
                ConversationAwareness = ca;
                ConversationAwarenessChanged?.Invoke(ca);
                break;
            case AapSpeechLevel s:
                SpeechLevelChanged?.Invoke(s.Level);
                break;
        }
    }

    public void Dispose()
    {
        _transport.Received -= OnPacket;
        _transport.Dispose();
    }
}
