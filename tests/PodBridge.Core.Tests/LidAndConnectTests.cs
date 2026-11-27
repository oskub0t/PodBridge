using PodBridge.Core.Ble;
using PodBridge.Core.Features;

namespace PodBridge.Core.Tests;

public class AdvertCadenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Ms(int ms) => T0.AddMilliseconds(ms);

    [Fact]
    public void Field_rhythm_of_127_ms_tolerates_scanning_pauses()
    {
        var c = new AdvertCadence();
        for (int i = 0; i <= 10; i++) c.Observe(Ms(i * 127)); // last at 1270 ms
        Assert.Equal(TimeSpan.FromSeconds(1.5), c.Threshold);  // 10 × 127 ms clamped up to the minimum
        Assert.False(c.LooksGone(Ms(1270 + 800)));             // a 0.8 s pause is not "gone"
        Assert.True(c.LooksGone(Ms(1270 + 1600)));
    }

    [Fact]
    public void Slower_rhythm_scales_and_caps()
    {
        var c = new AdvertCadence();
        for (int i = 0; i <= 10; i++) c.Observe(Ms(i * 200));
        Assert.Equal(TimeSpan.FromSeconds(2), c.Threshold);
        var d = new AdvertCadence();
        for (int i = 0; i <= 10; i++) d.Observe(Ms(i * 1000));
        Assert.Equal(TimeSpan.FromSeconds(3), d.Threshold);
    }

    [Fact]
    public void Duplicate_bursts_are_ignored()
    {
        var c = new AdvertCadence();
        for (int i = 0; i <= 10; i++) { c.Observe(Ms(i * 200)); c.Observe(Ms(i * 200 + 1)); }
        Assert.InRange(c.Threshold.TotalMilliseconds, 1980, 2000); // ~10 × 199 ms, not 10 × 1 ms
    }

    [Fact]
    public void Nothing_seen_is_not_gone() => Assert.False(new AdvertCadence().LooksGone(T0));
}

public class LidTrackerTests
{
    private static AirPodsStatus Field(string hex) =>
        ProximityPairingParser.TryParse(Convert.FromHexString(hex), -46, DateTimeOffset.UnixEpoch)!;

    // The user's capture with the lid closed (lid byte 0x3C) and the same advert with lid 0x34.
    private const string Closed = "07190127201588B63C000434AC5F4C95690298177BA4B46CA8DD1B";
    private const string Open   = "07190127201588B634000434AC5F4C95690298177BA4B46CA8DD1B";

    [Fact]
    public void Field_bytes_decode_lid_state()
    {
        Assert.Equal(0x3C, Field(Closed).RawLid);
        Assert.False(LidTracker.IsOpen(Field(Closed)));
        Assert.True(LidTracker.IsOpen(Field(Open)));
    }

    [Fact]
    public void Field_sequence_open_close_open_close()
    {
        var t = new LidTracker();
        var seq = new[] { 0x33, 0x33, 0x3B, 0x3B, 0x34, 0x34, 0x3C, 0x3C };
        var events = seq.Select(lid => t.Observe(new AirPodsStatus(0x2720, 80, 80, 60, true, true, false, false, false, -46,
            DateTimeOffset.UnixEpoch) { RawLid = (byte)lid })).ToList();
        Assert.Equal(new[] { LidEvent.None, LidEvent.None, LidEvent.Closed, LidEvent.None,
                             LidEvent.Opened, LidEvent.None, LidEvent.Closed, LidEvent.None }, events);
    }

    [Fact]
    public void Pod_out_of_the_case_does_not_flicker_the_lid()
    {
        // 16.16.54 in the field: one pod taken out (lid 0x13) while the other, still in the closed
        // case, reports 0x3B. Interleaved, they must not read as open/closed/open/closed.
        var t = new LidTracker();
        AirPodsStatus L(int lid) => new(0x2720, 80, 80, 60, true, false, false, false, false, -46,
            DateTimeOffset.UnixEpoch) { RawLid = (byte)lid };
        var events = new[] { 0x33, 0x13, 0x3B, 0x13, 0x3B, 0x13, 0x31 }.Select(x => t.Observe(L(x))).ToList();
        Assert.Equal(new[] { LidEvent.None, LidEvent.None, LidEvent.Closed, LidEvent.None,
                             LidEvent.None, LidEvent.None, LidEvent.Opened }, events);
    }

    [Fact]
    public void Reset_makes_the_next_reading_a_fresh_start()
    {
        var t = new LidTracker();
        t.Observe(Field(Closed));
        t.Reset();
        Assert.Equal(LidEvent.None, t.Observe(Field(Open)));
    }
}

public class AutoConnectPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Opening_the_case_never_connects_by_default()
    {
        var cfg = new PodBridgeConfig();
        var p = new AutoConnectPolicy();
        Assert.Equal(ConnectTrigger.PutInEar, cfg.ConnectWhen);
        Assert.False(p.OnCaseOpened(cfg, false, false, T0));
    }

    [Fact]
    public void First_reading_is_only_a_baseline_even_if_it_says_in_ear()
    {
        // The field bug: AirPods charging in an open case were read as "in ear" and connected.
        var p = new AutoConnectPolicy();
        Assert.False(p.OnInEarCount(new PodBridgeConfig(), 1, false, false, T0));
        Assert.False(p.OnInEarCount(new PodBridgeConfig(), 1, false, false, T0.AddSeconds(1)));
    }

    [Fact]
    public void Putting_a_bud_in_connects()
    {
        var cfg = new PodBridgeConfig();
        var p = new AutoConnectPolicy();
        p.OnInEarCount(cfg, 0, false, false, T0);
        Assert.True(p.OnInEarCount(cfg, 1, false, false, T0.AddSeconds(2)));
    }

    [Fact]
    public void Case_open_mode_connects_on_open_and_ignores_ears()
    {
        var cfg = new PodBridgeConfig { ConnectWhen = ConnectTrigger.CaseOpens };
        var p = new AutoConnectPolicy();
        p.OnInEarCount(cfg, 0, false, false, T0);
        Assert.False(p.OnInEarCount(cfg, 2, false, false, T0));
        Assert.True(p.OnCaseOpened(cfg, false, false, T0.AddSeconds(20)));
    }

    [Fact]
    public void Cooldown_and_off_switch()
    {
        var cfg = new PodBridgeConfig();
        var p = new AutoConnectPolicy();
        p.OnInEarCount(cfg, 0, false, false, T0);
        Assert.True(p.OnInEarCount(cfg, 1, false, false, T0));
        p.OnInEarCount(cfg, 0, false, false, T0.AddSeconds(2));
        Assert.False(p.OnInEarCount(cfg, 1, false, false, T0.AddSeconds(5)));   // within cooldown
        p.OnInEarCount(cfg, 0, false, false, T0.AddSeconds(18));
        Assert.True(p.OnInEarCount(cfg, 1, false, false, T0.AddSeconds(20)));

        var off = new PodBridgeConfig { AutoConnect = false };
        var q = new AutoConnectPolicy();
        q.OnInEarCount(off, 0, false, false, T0);
        Assert.False(q.OnInEarCount(off, 2, false, false, T0));
    }

    [Theory]
    [InlineData(true, false)]   // already connected
    [InlineData(false, true)]   // user just disconnected
    public void Respects_state(bool connected, bool recentlyDisconnected)
    {
        var p = new AutoConnectPolicy();
        p.OnInEarCount(new PodBridgeConfig(), 0, connected, recentlyDisconnected, T0);
        Assert.False(p.OnInEarCount(new PodBridgeConfig(), 2, connected, recentlyDisconnected, T0));
    }

    [Fact]
    public void Reset_starts_a_new_baseline()
    {
        var p = new AutoConnectPolicy();
        p.OnInEarCount(new PodBridgeConfig(), 0, false, false, T0);
        p.ResetBaseline();
        Assert.False(p.OnInEarCount(new PodBridgeConfig(), 2, false, false, T0)); // baseline again
    }
}

public class ChargingMeansInCaseTests
{
    private static AirPodsStatus S(bool lIn, bool rIn, bool lCh, bool rCh) =>
        new(0x2720, 80, 80, 60, lCh, rCh, false, lIn, rIn, -50, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Charging_pods_are_never_in_an_ear() => Assert.Equal(0, S(true, true, true, true).InEarCount);

    [Fact]
    public void Worn_pods_count() => Assert.Equal(2, S(true, true, false, false).InEarCount);

    [Fact]
    public void One_out_charging_one_worn() => Assert.Equal(1, S(true, true, true, false).InEarCount);
}
