using PodBridge.Core.Audio;
using PodBridge.Core.Ble;
using PodBridge.Core.Calls;

namespace PodBridge.Core.Tests;

public class PresenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private static AirPodsStatus S(ushort model, short rssi, DateTimeOffset t) =>
        new(model, 80, 80, 50, false, false, false, false, false, rssi, t);

    [Fact]
    public void Colleagues_different_model_never_triggers_but_is_reported_as_candidate()
    {
        var cfg = new PodBridgeConfig { MyModelId = 0x1420 };
        var t = new PresenceTracker(() => cfg);
        int appeared = 0, candidates = 0;
        t.Appeared += _ => appeared++;
        t.Candidate += _ => candidates++;

        t.OnAdvertisement(S(0x1920, -45, T0)); // colleague's AirPods 4, right next to me
        Assert.Equal(0, appeared);
        Assert.Equal(1, candidates);

        t.OnAdvertisement(S(0x1420, -55, T0));
        Assert.Equal(1, appeared);
    }

    [Fact]
    public void Weak_signal_is_ignored()
    {
        var cfg = new PodBridgeConfig { MyModelId = 0x1420, MinRssi = -70 };
        var t = new PresenceTracker(() => cfg);
        int appeared = 0;
        t.Appeared += _ => appeared++;
        t.OnAdvertisement(S(0x1420, -85, T0)); // same model, other side of the office
        Assert.Equal(0, appeared);
    }

    [Fact]
    public void Appeared_fires_once_per_case_open_not_per_advert()
    {
        var cfg = new PodBridgeConfig { MyModelId = 0x1420, AbsenceSeconds = 30 };
        var t = new PresenceTracker(() => cfg);
        int appeared = 0;
        t.Appeared += _ => appeared++;

        for (int i = 0; i < 20; i++) t.OnAdvertisement(S(0x1420, -50, T0.AddSeconds(i)));
        Assert.Equal(1, appeared);

        t.OnAdvertisement(S(0x1420, -50, T0.AddMinutes(5))); // case closed, reopened later
        Assert.Equal(2, appeared);
    }

    [Fact]
    public void Nothing_is_mine_until_learned()
    {
        var t = new PresenceTracker(() => new PodBridgeConfig());
        Assert.False(t.IsMine(S(0x1420, -40, T0)));
    }
}

public class CallDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Teams_running_without_mic_is_not_a_call()
    {
        var d = new CallDetector(() => new PodBridgeConfig());
        d.Tick([new("MSTeams_8wekyb3d8bbwe", false)], T0);
        Assert.False(d.InCall);
    }

    [Fact]
    public void New_and_classic_teams_are_detected()
    {
        var d = new CallDetector(() => new PodBridgeConfig());
        string? app = null;
        d.CallStarted += a => app = a;
        d.Tick([new("C:#Users#me#AppData#Local#Microsoft#Teams#current#Teams.exe", true)], T0);
        Assert.True(d.InCall);
        Assert.Contains("Teams.exe", app);
    }

    [Fact]
    public void Other_apps_using_the_mic_are_ignored()
    {
        var d = new CallDetector(() => new PodBridgeConfig());
        d.Tick([new("Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe", true)], T0);
        Assert.False(d.InCall);
    }

    [Fact]
    public void Brief_mic_release_does_not_end_the_call()
    {
        var d = new CallDetector(() => new PodBridgeConfig()) { EndDelay = TimeSpan.FromSeconds(6) };
        int ended = 0;
        d.CallEnded += () => ended++;
        var on = new List<MicUsageEntry> { new("MSTeams_8wekyb3d8bbwe", true) };
        var off = new List<MicUsageEntry> { new("MSTeams_8wekyb3d8bbwe", false) };

        d.Tick(on, T0);
        d.Tick(off, T0.AddSeconds(1));   // Teams re-opening the device after we switched it
        d.Tick(off, T0.AddSeconds(3));
        d.Tick(on, T0.AddSeconds(4));
        d.Tick(off, T0.AddSeconds(5));
        d.Tick(off, T0.AddSeconds(10));
        Assert.Equal(0, ended);
        Assert.True(d.InCall);

        d.Tick(off, T0.AddSeconds(12));
        Assert.Equal(1, ended);
        Assert.False(d.InCall);
    }
}

public class MicWatchdogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Warns_once_after_sustained_digital_silence()
    {
        var w = new MicWatchdog();
        Assert.False(w.Tick(0f, T0, 20));
        Assert.False(w.Tick(0f, T0.AddSeconds(19), 20));
        Assert.True(w.Tick(0f, T0.AddSeconds(20), 20));
        Assert.False(w.Tick(0f, T0.AddSeconds(40), 20));
    }

    [Fact]
    public void Quiet_room_noise_is_not_silence()
    {
        var w = new MicWatchdog();
        for (int i = 0; i < 60; i++)
            Assert.False(w.Tick(i % 2 == 0 ? 0f : 0.003f, T0.AddSeconds(i), 20));
    }
}
