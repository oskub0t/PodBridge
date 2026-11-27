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

/// <summary>Real advertisements captured on the user's PC (Windows 11 26200, AirPods Pro 3 nearby).</summary>
public class CapturedAdvertTests
{
    private static byte[] Hex(string h) => Convert.FromHexString(h);

    [Fact]
    public void Plain_proximity_pairing_from_the_field_decodes()
    {
        var s = ProximityPairingParser.TryParse(
            Hex("071901272023AA8F110004309D09AAC688E512A738A5247D475CE7"), -55, DateTimeOffset.UnixEpoch)!;
        Assert.Equal(0x2720, s.ModelId);
        Assert.Equal(100, s.LeftPercent);
        Assert.Equal(100, s.RightPercent);
        Assert.Null(s.CasePercent);
        Assert.True(AirPodsModels.LooksValid(s.ModelId));
        Assert.Equal(0x23, s.RawStatus); // kept for in-ear mapping diagnostics
    }

    [Fact]
    public void Second_pair_nearby_decodes_as_worn_at_80()
    {
        var s = ProximityPairingParser.TryParse(
            Hex("07190136200B888F11000584642F10503CB3F987FE3AF85E876E3F"), -59, DateTimeOffset.UnixEpoch)!;
        Assert.Equal(0x3620, s.ModelId);
        Assert.Equal(80, s.LeftPercent);
        Assert.Equal(80, s.RightPercent);
        Assert.True(s.LeftInEar && s.RightInEar);
    }

    [Theory]
    [InlineData("071106EEE7CD39FF6EE2874D7D3B82F8EF38B9")]
    [InlineData("071106ED9B459C1F5F4F568C524CFAD9DF50C8")]
    [InlineData("071106917048FA8041166B0783E4765F2D6F60")]
    [InlineData("07110636DFDB21CB0A4D55A3849DECE7B540C3")]
    public void Encrypted_variant_is_ignored(string hex) =>
        Assert.Null(ProximityPairingParser.TryParse(Hex(hex), -63, DateTimeOffset.UnixEpoch));

    [Theory]
    [InlineData((ushort)0x26B0, false)] // what v0.2.0 saved from an encrypted advert
    [InlineData((ushort)0x2720, true)]
    [InlineData((ushort)0x1420, true)]
    public void Saved_ids_are_sanity_checked(ushort id, bool valid) =>
        Assert.Equal(valid, AirPodsModels.LooksValid(id));

    [Theory]
    [InlineData("Alex’s AirPods Pro - Find My", "Alex’s AirPods Pro")]
    [InlineData("AirPods Pro", "AirPods Pro")]
    public void Find_my_suffix_is_hidden(string name, string shown) =>
        Assert.Equal(shown, PodBridge.Core.Audio.EndpointResolver.DisplayName(name));
}

public class InCaseFieldCaptureTests
{
    // Both AirPods Pro 3 charging in an open case: status alternates 0x15 / 0x75 as each pod
    // advertises. Neither must read as "in ear".
    [Theory]
    [InlineData(0x15)]
    [InlineData(0x75)]
    public void Pods_charging_in_case_are_not_in_ear(byte status)
    {
        var bytes = Convert.FromHexString("07190127201588B634000434AC5F4C95690298177BA4B46CA8DD1B");
        bytes[5] = status;
        var s = ProximityPairingParser.TryParse(bytes, -46, DateTimeOffset.UnixEpoch)!;
        Assert.Equal(0, s.InEarCount);
        Assert.Equal(80, s.LeftPercent);
        Assert.Equal(80, s.RightPercent);
        Assert.True(s.LeftCharging && s.RightCharging);
    }
}

public class WornFieldCaptureTests
{
    // AirPods Pro 3, worn, from the user's test: both in → right out → both in → left out.
    private static AirPodsStatus WithStatus(byte status)
    {
        var bytes = Convert.FromHexString("071901272003888F130004B54EDA504A6ECDE3EB0C9C8DD037E473");
        bytes[5] = status;
        return ProximityPairingParser.TryParse(bytes, -53, DateTimeOffset.UnixEpoch)!;
    }

    [Theory]
    [InlineData(0x0B, true, true)]
    [InlineData(0x2B, true, true)]
    [InlineData(0x09, true, false)]   // right taken out
    [InlineData(0x23, true, false)]   // same, other pod's perspective
    [InlineData(0x29, false, true)]   // left taken out
    [InlineData(0x03, false, true)]   // same, other pod's perspective
    public void In_ear_bits_map_to_the_right_bud(byte status, bool left, bool right)
    {
        var s = WithStatus(status);
        Assert.Equal(left, s.LeftInEar);
        Assert.Equal(right, s.RightInEar);
        Assert.False(s.LeftCharging || s.RightCharging);
        Assert.Equal((left ? 1 : 0) + (right ? 1 : 0), s.InEarCount);
    }
}
