using PodBridge.Core.Aap;
using PodBridge.Core.Features;

namespace PodBridge.Core.Tests;

// These check our encoding/decoding against the publicly documented formats. They can't prove the
// formats are right for real AirPods; only the driver + hardware can (docs/advanced-features.md).
public class AapCodecTests
{
    private static byte[] Hex(string h) => Convert.FromHexString(h.Replace(" ", ""));

    [Fact]
    public void Commands_encode_as_documented()
    {
        Assert.Equal(Hex("04 00 04 00 09 00 0D 03 00 00 00"), AapCodec.SetNoiseMode(NoiseMode.Transparency));
        Assert.Equal(Hex("04 00 04 00 09 00 0D 04 00 00 00"), AapCodec.SetNoiseMode(NoiseMode.Adaptive));
        Assert.Equal(Hex("04 00 04 00 09 00 28 01 00 00 00"), AapCodec.SetConversationAwareness(true));
        Assert.Equal(Hex("04 00 04 00 09 00 28 02 00 00 00"), AapCodec.SetConversationAwareness(false));
    }

    [Fact]
    public void Battery_decodes_per_component()
    {
        var b = Assert.IsType<AapBattery>(AapCodec.Parse(Hex(
            "04 00 04 00 04 00 03  02 01 64 02 01  04 01 63 01 01  08 01 11 02 01")));
        Assert.Equal(100, b.Get(BatteryComponent.Right)!.Percent);
        Assert.Equal(99, b.Get(BatteryComponent.Left)!.Percent);
        Assert.Equal(ChargeState.Charging, b.Get(BatteryComponent.Left)!.State);
        Assert.Equal(17, b.Get(BatteryComponent.Case)!.Percent);
    }

    [Fact]
    public void Ear_state_counts_pods_in_ear()
    {
        var e = Assert.IsType<AapEarState>(AapCodec.Parse(Hex("04 00 04 00 06 00 00 01")));
        Assert.Equal(1, e.InEarCount);
        Assert.Equal(0, ((AapEarState)AapCodec.Parse(Hex("04 00 04 00 06 00 02 02"))!).InEarCount);
    }

    [Fact]
    public void Control_values_are_recognised()
    {
        var c = Assert.IsType<AapControl>(AapCodec.Parse(Hex("04 00 04 00 09 00 0D 02 00 00 00")));
        Assert.Equal(NoiseMode.NoiseCancellation, c.AsNoiseMode);
        Assert.Null(c.AsConversationAwareness);
        Assert.True(((AapControl)AapCodec.Parse(Hex("04 00 04 00 09 00 28 01 00 00 00"))!).AsConversationAwareness);
    }

    [Theory]
    [InlineData("")]
    [InlineData("04 00 04")]
    [InlineData("05 00 04 00 04 00 01 02 01 64 02 01")]   // wrong header
    [InlineData("04 00 04 00 04 00 02 02 01 64 02 01")]   // says 2 entries, has 1
    [InlineData("04 00 04 00 04 00 01 02 01 C8 02 01")]   // 200 %
    [InlineData("04 00 04 00 06 00 07 00")]               // unknown placement
    [InlineData("04 00 04 00 77 00 01 02 03")]            // unknown opcode
    public void Unknown_or_malformed_is_ignored(string hex) => Assert.Null(AapCodec.Parse(Hex(hex)));
}

public class AapSessionTests
{
    private sealed class FakeTransport : IAapTransport
    {
        public List<byte[]> Sent { get; } = [];
        public event Action<byte[]>? Received;
        public event Action? Closed;
        public Task SendAsync(byte[] p) { Sent.Add(p); return Task.CompletedTask; }
        public void Deliver(string hex) => Received?.Invoke(Convert.FromHexString(hex.Replace(" ", "")));
        public void Close() => Closed?.Invoke();
        public void Dispose() { }
    }

    [Fact]
    public async Task Start_sends_handshake_features_then_subscription()
    {
        var t = new FakeTransport();
        await new AapSession(t).StartAsync();
        Assert.Equal([AapCodec.Handshake, AapCodec.SetFeatureFlags, AapCodec.RequestNotifications], t.Sent);
    }

    [Fact]
    public void Incoming_packets_update_state_and_raise_events()
    {
        var t = new FakeTransport();
        var s = new AapSession(t);
        NoiseMode? mode = null; int? inEar = null; bool closed = false;
        s.NoiseModeChanged += m => mode = m;
        s.InEarCountChanged += n => inEar = n;
        s.Closed += () => closed = true;

        t.Deliver("04 00 04 00 09 00 0D 04 00 00 00");
        t.Deliver("04 00 04 00 06 00 00 00");
        t.Deliver("04 00 04 00 04 00 01 04 01 37 02 01");
        t.Close();

        Assert.Equal(NoiseMode.Adaptive, mode);
        Assert.Equal(2, inEar);
        Assert.Equal(55, s.Battery!.Get(BatteryComponent.Left)!.Percent);
        Assert.True(closed);
    }

    [Fact]
    public void Repeated_ear_state_raises_once()
    {
        var t = new FakeTransport();
        var s = new AapSession(t);
        int raised = 0;
        s.InEarCountChanged += _ => raised++;
        t.Deliver("04 00 04 00 06 00 00 00");
        t.Deliver("04 00 04 00 06 00 00 00");
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Commands_go_out_unchanged()
    {
        var t = new FakeTransport();
        var s = new AapSession(t);
        await s.SetNoiseModeAsync(NoiseMode.Off);
        Assert.Equal(AapCodec.SetNoiseMode(NoiseMode.Off), t.Sent.Single());
    }
}

public class ExactEarEventTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Exact_events_act_immediately()
    {
        var e = new EarDetection();
        Assert.Equal(EarAction.None, e.ObserveExact(2, T0));
        Assert.Equal(EarAction.Pause, e.ObserveExact(1, T0.AddSeconds(1)));
        Assert.Equal(EarAction.Resume, e.ObserveExact(2, T0.AddSeconds(5)));
    }

    [Fact]
    public void Advert_confirming_an_exact_event_does_nothing_twice()
    {
        var e = new EarDetection();
        e.ObserveExact(2, T0);
        Assert.Equal(EarAction.Pause, e.ObserveExact(1, T0.AddSeconds(1)));
        Assert.Equal(EarAction.None, e.Observe(1, T0.AddSeconds(2)));
        Assert.Equal(EarAction.None, e.Observe(1, T0.AddSeconds(3)));
    }

    [Fact]
    public void Advanced_features_are_off_by_default() =>
        Assert.False(new PodBridgeConfig().AdvancedFeatures);
}
