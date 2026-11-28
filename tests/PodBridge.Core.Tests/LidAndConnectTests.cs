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
        var events = new[] { 0x33, 0x13, 0x3B, 0x13, 0x3B, 0x13, 0x34 }.Select(x => t.Observe(L(x))).ToList();
        Assert.Equal(new[] { LidEvent.None, LidEvent.None, LidEvent.Closed, LidEvent.None,
                             LidEvent.None, LidEvent.None, LidEvent.Opened }, events);
    }

    private static AirPodsStatus Pod(char pod, int lid, DateTimeOffset at) =>
        new(0x2720, 80, 80, 60, true, true, false, false, false, -43, at)
        { RawStatus = (byte)(pod == 'L' ? 0x35 : 0x55), RawLid = (byte)lid };

    [Fact]
    public void Late_reports_from_the_other_pod_are_ignored()
    {
        // The 0.4.4 field log (12.10.37–12.11.21), in-case adverts only, both pods interleaved
        // (about 2 s apart). Old behaviour: the right pod's 0x37 (opening #7) showed the card, then
        // the left pod's late 0x3E (closed, opening #6) dismissed it straight away.
        var t = new LidTracker();
        var t0 = new DateTimeOffset(2026, 11, 28, 12, 10, 37, TimeSpan.Zero);
        (char, int)[] log = [('R', 0x31), ('L', 0x31), ('R', 0x31), ('L', 0x39), ('R', 0x32), ('L', 0x32),
            ('R', 0x3A), ('L', 0x33), ('R', 0x3B), ('L', 0x34), ('R', 0x3C), ('L', 0x35), ('R', 0x3D),
            ('L', 0x36), ('R', 0x3E), ('R', 0x37), ('L', 0x3E), ('L', 0x37), ('R', 0x3F), ('L', 0x30),
            ('R', 0x38), ('L', 0x31), ('R', 0x39)];
        var events = log.Select((x, i) => t.Observe(Pod(x.Item1, x.Item2, t0.AddSeconds(i * 1.9))))
            .ToList();

        Assert.Equal(LidEvent.Opened, events[15]); // right pod: opening #7
        Assert.Equal(LidEvent.None, events[16]);   // left pod's late 'closed' from #6: ignored
        var real = events.Where(e => e != LidEvent.None).ToList();
        Assert.Equal(LidEvent.Closed, real[0]);
        for (int i = 1; i < real.Count; i++) Assert.NotEqual(real[i - 1], real[i]);
        Assert.Equal(8, real.Count(e => e == LidEvent.Opened)); // #2 … #7, #0, #1
    }

    [Fact]
    public void Pods_with_different_counters_still_show_every_opening()
    {
        // The 0.4.6 field log (13.28.03–13.29.03), lid flipped repeatedly. The pods' counters
        // disagree (left says #1 while right says #2) and the left one went 3 → 1 while the case
        // lay closed. 0.4.5 ordered reports by counter across pods and ignored everything from
        // 13.28.27 to 13.28.51, including the opening at 13.28.44 after 12 s closed: no card.
        (string at, char pod, int lid)[] log =
        [
            ("13.28.04", 'L', 0x33), ("13.28.05", 'L', 0x3B), ("13.28.06", 'L', 0x34), ("13.28.08", 'R', 0x3A),
            ("13.28.09", 'R', 0x33), ("13.28.12", 'R', 0x3B), ("13.28.12", 'R', 0x34), ("13.28.15", 'R', 0x3C),
            ("13.28.17", 'L', 0x37), ("13.28.18", 'L', 0x3F), ("13.28.19", 'R', 0x36), ("13.28.21", 'L', 0x38),
            ("13.28.22", 'R', 0x37), ("13.28.24", 'L', 0x39), ("13.28.25", 'L', 0x32), ("13.28.26", 'L', 0x3A),
            ("13.28.27", 'R', 0x31), ("13.28.30", 'L', 0x3B), ("13.28.30", 'R', 0x32), ("13.28.32", 'R', 0x3A),
            ("13.28.44", 'L', 0x31), ("13.28.48", 'L', 0x39), ("13.28.48", 'R', 0x32), ("13.28.48", 'L', 0x39),
            ("13.28.49", 'R', 0x32), ("13.28.51", 'R', 0x3A), ("13.28.51", 'R', 0x33), ("13.28.54", 'L', 0x3B),
            ("13.28.55", 'R', 0x34), ("13.28.59", 'R', 0x3C), ("13.29.00", 'R', 0x35), ("13.29.03", 'R', 0x3D),
        ];
        var t = new LidTracker();
        var events = log.Select((x, i) =>
        {
            var p = x.at.Split('.').Select(int.Parse).ToArray();
            var at = new DateTimeOffset(2026, 11, 28, p[0], p[1], p[2], TimeSpan.Zero).AddMilliseconds(i * 10);
            return (x.at, e: t.Observe(Pod(x.pod, x.lid, at)));
        }).Where(x => x.e != LidEvent.None).ToList();

        Assert.Contains(("13.28.44", LidEvent.Opened), events); // the opening 0.4.5 missed
        Assert.Contains(("13.28.55", LidEvent.Opened), events);
        Assert.Contains(("13.29.03", LidEvent.Closed), events);
        for (int i = 1; i < events.Count; i++) Assert.NotEqual(events[i - 1].e, events[i].e);
        // Keeps following the flips instead of going deaf (0.4.5 reported 9 events here).
        Assert.True(events.Count >= 12, string.Join(", ", events));
    }

    [Fact]
    public void Late_open_from_the_other_pod_does_not_reopen_the_card()
    {
        var t = new LidTracker();
        var t0 = DateTimeOffset.UnixEpoch;
        t.Observe(Pod('R', 0x3B, t0));                                                 // closed
        Assert.Equal(LidEvent.Opened, t.Observe(Pod('R', 0x34, t0.AddSeconds(1))));    // #4 opens
        Assert.Equal(LidEvent.Closed, t.Observe(Pod('R', 0x3C, t0.AddSeconds(2))));    // and closes
        Assert.Equal(LidEvent.None, t.Observe(Pod('L', 0x34, t0.AddSeconds(2.3))));    // left, late
        Assert.NotNull(t.LastIgnored);
        Assert.Equal(LidEvent.Opened, t.Observe(Pod('R', 0x35, t0.AddSeconds(4))));    // #5: real
    }

    [Fact]
    public void A_lagging_pod_is_trusted_after_the_grace_period()
    {
        var t = new LidTracker();
        var t0 = DateTimeOffset.UnixEpoch;
        t.Observe(Pod('R', 0x3B, t0));
        Assert.Equal(LidEvent.Opened, t.Observe(Pod('R', 0x34, t0.AddSeconds(1))));
        Assert.Equal(LidEvent.None, t.Observe(Pod('L', 0x3A, t0.AddSeconds(1.5))));   // within grace
        Assert.Equal(LidEvent.Closed, t.Observe(Pod('L', 0x3B, t0.AddSeconds(5))));   // much later
    }

    [Fact]
    public void Only_adverts_that_count_as_yours_feed_the_lid()
    {
        var weak = Pod('R', 0x34, DateTimeOffset.UnixEpoch) with { Rssi = -74 };
        // Your model, too weak to count as yours: ignored even while your card is open.
        Assert.False(LidTracker.Feeds(weak, mine: false, myModel: 0x2720, cardCaseModel: 0x2720));
        Assert.True(LidTracker.Feeds(weak with { Rssi = -55 }, mine: true, myModel: 0x2720, cardCaseModel: null));
        // Another model only while its own card is open (learn mode).
        var other = weak with { ModelId = 0x3620 };
        Assert.True(LidTracker.Feeds(other, mine: false, myModel: 0x2720, cardCaseModel: 0x3620));
        Assert.False(LidTracker.Feeds(other, mine: false, myModel: 0x2720, cardCaseModel: 0x2720));
        Assert.True(LidTracker.Feeds(other, mine: false, myModel: null, cardCaseModel: 0x3620));
    }

    [Fact]
    public void Counter_wraps_from_7_to_0()
    {
        var t = new LidTracker();
        AirPodsStatus L(int lid) => new(0x2720, 80, 80, 60, true, true, false, false, false, -43,
            DateTimeOffset.UnixEpoch) { RawLid = (byte)lid };
        t.Observe(L(0x3F));                                // closed, opening #7
        Assert.Equal(LidEvent.Opened, t.Observe(L(0x30))); // opening #0 is newer
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
