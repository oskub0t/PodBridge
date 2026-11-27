using PodBridge.Core.Ble;
using PodBridge.Core.Features;

namespace PodBridge.Core.Tests;

public class AdvertCadenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Ms(int ms) => T0.AddMilliseconds(ms);

    [Fact]
    public void Fast_advertiser_is_declared_gone_quickly()
    {
        var c = new AdvertCadence();
        for (int i = 0; i <= 10; i++) c.Observe(Ms(i * 100)); // every 100 ms, last at 1000 ms
        Assert.Equal(TimeSpan.FromMilliseconds(400), c.Threshold);
        Assert.False(c.LooksGone(Ms(1300)));
        Assert.True(c.LooksGone(Ms(1450)));
    }

    [Fact]
    public void Never_faster_than_the_minimum()
    {
        var c = new AdvertCadence();
        for (int i = 0; i <= 10; i++) c.Observe(Ms(i * 20));
        Assert.Equal(TimeSpan.FromMilliseconds(350), c.Threshold);
    }

    [Fact]
    public void Slow_or_unknown_rhythm_falls_back_to_max()
    {
        var c = new AdvertCadence();
        c.Observe(Ms(0));
        Assert.Equal(TimeSpan.FromSeconds(2.5), c.Threshold);
        for (int i = 1; i <= 10; i++) c.Observe(Ms(i * 1000));
        Assert.Equal(TimeSpan.FromSeconds(2.5), c.Threshold);
    }

    [Fact]
    public void Duplicate_bursts_do_not_shrink_the_threshold()
    {
        var c = new AdvertCadence();
        for (int i = 0; i <= 10; i++) { c.Observe(Ms(i * 200)); c.Observe(Ms(i * 200 + 1)); }
        // gaps are ~199 ms (the 1 ms duplicates are ignored), so ~4 × 199 ms, not 4 × 1 ms
        Assert.InRange(c.Threshold.TotalMilliseconds, 780, 800);
    }

    [Fact]
    public void Nothing_seen_is_not_gone() => Assert.False(new AdvertCadence().LooksGone(T0));
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
