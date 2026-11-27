using System.Text.Json;
using PodBridge.Core.Ble;
using PodBridge.Core.Features;

namespace PodBridge.Core.Tests;

public class EarDetectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double s) => T0.AddSeconds(s);

    [Fact]
    public void Removing_one_pauses_and_putting_back_resumes()
    {
        var e = new EarDetection();
        Assert.Equal(EarAction.None, e.Observe(2, At(0)));
        Assert.Equal(EarAction.None, e.Observe(1, At(1)));    // first sighting
        Assert.Equal(EarAction.Pause, e.Observe(1, At(2)));   // confirmed
        Assert.Equal(EarAction.None, e.Observe(2, At(30)));
        Assert.Equal(EarAction.Resume, e.Observe(2, At(31)));
    }

    [Fact]
    public void Single_glitch_is_ignored()
    {
        var e = new EarDetection();
        e.Observe(2, At(0));
        Assert.Equal(EarAction.None, e.Observe(0, At(1)));
        Assert.Equal(EarAction.None, e.Observe(2, At(2)));
        Assert.Equal(EarAction.None, e.Observe(2, At(3)));
        Assert.Equal(2, e.InEarCount);
    }

    [Fact]
    public void Putting_in_after_a_long_break_does_not_resume()
    {
        var e = new EarDetection { ResumeWindow = TimeSpan.FromMinutes(10) };
        e.Observe(2, At(0)); e.Observe(0, At(1)); e.Observe(0, At(2));
        e.Observe(2, At(60 * 20));
        Assert.Equal(EarAction.None, e.Observe(2, At(60 * 20 + 1)));
    }

    [Fact]
    public void Putting_them_in_fresh_does_not_start_playback()
    {
        var e = new EarDetection();
        e.Observe(0, At(0));
        e.Observe(2, At(1));
        Assert.Equal(EarAction.None, e.Observe(2, At(2)));
    }

    [Fact]
    public void Single_bud_listening_works()
    {
        var e = new EarDetection();
        e.Observe(1, At(0));
        e.Observe(0, At(1));
        Assert.Equal(EarAction.Pause, e.Observe(0, At(2)));
        e.Observe(1, At(5));
        Assert.Equal(EarAction.Resume, e.Observe(1, At(6)));
    }
}

public class LowBatteryTests
{
    private static AirPodsStatus S(int? l, int? r, bool lc = false, bool rc = false) =>
        new(0x1420, l, r, 50, lc, rc, false, true, true, -50, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Alerts_at_20_then_10_once_each()
    {
        var m = new LowBatteryMonitor();
        Assert.Null(m.Observe(S(30, 30)));
        var a = m.Observe(S(20, 30));
        Assert.Equal(20, a!.Threshold);
        Assert.True(a.Left);
        Assert.False(a.Right);
        Assert.Null(m.Observe(S(20, 30)));
        Assert.Null(m.Observe(S(20, 30)));
        Assert.Equal(10, m.Observe(S(10, 30))!.Threshold);
        Assert.Null(m.Observe(S(0, 30)));
    }

    [Fact]
    public void Both_crossing_together_is_one_alert()
    {
        var m = new LowBatteryMonitor();
        var a = m.Observe(S(20, 10))!;
        Assert.True(a.Left && a.Right);
        Assert.Equal(10, a.Threshold);
        Assert.Equal(10, a.Percent);
    }

    [Fact]
    public void Charging_rearms()
    {
        var m = new LowBatteryMonitor();
        Assert.NotNull(m.Observe(S(20, null)));
        Assert.Null(m.Observe(S(20, null, lc: true)));
        Assert.NotNull(m.Observe(S(20, null)));
    }
}

public class SwitchAdvisorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private static AirPodsStatus Worn(DateTimeOffset t, bool inEar = true) =>
        new(0x1420, 80, 80, 50, false, false, false, inEar, inEar, -50, t);

    [Fact]
    public void Offers_when_worn_elsewhere_and_respects_cooldown()
    {
        var a = new SwitchAdvisor();
        Assert.Equal(SwitchDecision.Offer, a.OnPcAudioStarted(SwitchPolicy.Ask, Worn(T0), false, false, T0));
        Assert.Equal(SwitchDecision.None, a.OnPcAudioStarted(SwitchPolicy.Ask, Worn(T0), false, false, T0.AddMinutes(1)));
        Assert.Equal(SwitchDecision.Offer, a.OnPcAudioStarted(SwitchPolicy.Ask, Worn(T0.AddMinutes(6)), false, false, T0.AddMinutes(6)));
    }

    [Theory]
    [InlineData(SwitchPolicy.Never, true, false, false)]   // policy off
    [InlineData(SwitchPolicy.Ask, false, false, false)]    // not in ears
    [InlineData(SwitchPolicy.Ask, true, true, false)]      // already connected here
    [InlineData(SwitchPolicy.Ask, true, false, true)]      // user just disconnected
    public void Stays_quiet(SwitchPolicy p, bool worn, bool connected, bool recentlyDisconnected) =>
        Assert.Equal(SwitchDecision.None,
            new SwitchAdvisor().OnPcAudioStarted(p, Worn(T0, worn), connected, recentlyDisconnected, T0));

    [Fact]
    public void Stale_advert_is_not_trusted() =>
        Assert.Equal(SwitchDecision.None,
            new SwitchAdvisor().OnPcAudioStarted(SwitchPolicy.Ask, Worn(T0), false, false, T0.AddMinutes(2)));

    [Fact]
    public void Automatic_policy_connects() =>
        Assert.Equal(SwitchDecision.Connect,
            new SwitchAdvisor().OnPcAudioStarted(SwitchPolicy.Automatically, Worn(T0), false, false, T0));

    [Fact]
    public void Policy_round_trips_as_readable_json()
    {
        var json = JsonSerializer.Serialize(new PodBridgeConfig { ConnectToThisPc = SwitchPolicy.Automatically });
        Assert.Contains("\"Automatically\"", json);
        Assert.Equal(SwitchPolicy.Automatically, JsonSerializer.Deserialize<PodBridgeConfig>(json)!.ConnectToThisPc);
    }
}

public class ModelSightingsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static AirPodsStatus S(ushort model, double sec) =>
        new(model, null, null, null, false, false, false, false, false, -50, T0.AddSeconds(sec));

    [Fact]
    public void Stable_id_qualifies_after_three_sightings()
    {
        var m = new ModelSightings();
        Assert.False(m.Observe(S(0x1420, 0)));
        Assert.False(m.Observe(S(0x1420, 1)));
        Assert.True(m.Observe(S(0x1420, 2)));
    }

    [Fact]
    public void Changing_ids_never_qualify()
    {
        var m = new ModelSightings();
        var rnd = new Random(1);
        for (int i = 0; i < 200; i++)
            Assert.False(m.Observe(S((ushort)rnd.Next(0x10000), i * 0.5)));
    }

    [Fact]
    public void Sightings_too_far_apart_do_not_add_up()
    {
        var m = new ModelSightings { Window = TimeSpan.FromSeconds(20) };
        m.Observe(S(0x1420, 0)); m.Observe(S(0x1420, 10));
        Assert.False(m.Observe(S(0x1420, 30)));
    }
}

public class RawHexTests
{
    [Fact]
    public void Parser_keeps_raw_payload_for_diagnostics()
    {
        var data = new byte[] { 0x07, 0x07, 0x01, 0x0E, 0x20, 0x20, 0x55, 0x05, 0x01 };
        var s = ProximityPairingParser.TryParse(data, -50, DateTimeOffset.UnixEpoch)!;
        Assert.Equal("0707010E2020550501", s.RawHex);
    }
}
