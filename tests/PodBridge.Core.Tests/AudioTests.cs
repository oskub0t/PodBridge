using PodBridge.Core.Audio;

namespace PodBridge.Core.Tests;

public class FakeBackend : IAudioBackend
{
    public List<AudioEndpoint> Endpoints { get; } = [];
    public Dictionary<(Flow, Role), string> Defaults { get; } = [];

    public IReadOnlyList<AudioEndpoint> GetEndpoints() => Endpoints;
    public string? GetDefaultId(Flow flow, Role role) => Defaults.GetValueOrDefault((flow, role));
    public void SetDefault(string id, Role role)
    {
        var e = Endpoints.Single(x => x.Id == id);
        Defaults[(e.Flow, role)] = id;
    }
}

public class EndpointResolverTests
{
    [Theory]
    [InlineData("Headphones (AirPods Pro)", "AirPods Pro")]
    [InlineData("Headset (AirPods Pro Hands-Free AG Audio)", "AirPods Pro")]
    [InlineData("Headset (Sam’s AirPods Pro Hands-Free)", "Sam’s AirPods Pro")]
    [InlineData("Headphones (AirPods Pro Stereo)", "AirPods Pro")]
    [InlineData("Microphone (Realtek(R) Audio)", "Realtek(R) Audio")]
    public void Extracts_device_name(string endpoint, string expected) =>
        Assert.Equal(expected, EndpointResolver.BaseDeviceName(endpoint));

    [Fact]
    public void Win10_style_two_outputs_and_mic()
    {
        var eps = new List<AudioEndpoint>
        {
            new("spk", "Speakers (Realtek(R) Audio)", Flow.Render, true),
            new("a2dp", "Headphones (AirPods Pro)", Flow.Render, true),
            new("hfp", "Headset (AirPods Pro Hands-Free AG Audio)", Flow.Render, true),
            new("mic", "Headset (AirPods Pro Hands-Free AG Audio)", Flow.Capture, true),
            new("lapmic", "Microphone Array (Realtek(R) Audio)", Flow.Capture, true),
            new("other", "Headphones (AirPods 4)", Flow.Render, false), // colleague once paired here
        };
        var r = EndpointResolver.Resolve(eps, new PodBridgeConfig { DeviceName = "AirPods Pro" })!;
        Assert.Equal("a2dp", r.MusicOutput!.Id);
        Assert.Equal("hfp", r.CallOutput!.Id);
        Assert.Equal("mic", r.Mic!.Id);
    }

    [Fact]
    public void Unified_endpoint_has_no_separate_call_output()
    {
        var eps = new List<AudioEndpoint>
        {
            new("one", "Headphones (AirPods Pro)", Flow.Render, true),
            new("mic", "Headset (AirPods Pro)", Flow.Capture, true),
        };
        var r = EndpointResolver.Resolve(eps, new PodBridgeConfig())!;
        Assert.Equal("AirPods Pro", r.DeviceName);
        Assert.Equal("one", r.MusicOutput!.Id);
        Assert.Null(r.CallOutput);
        Assert.Equal("one", r.CallOutputOrMusic!.Id);
    }

    [Fact]
    public void No_airpods_returns_null() =>
        Assert.Null(EndpointResolver.Resolve(
            [new AudioEndpoint("spk", "Speakers (Realtek(R) Audio)", Flow.Render, true)],
            new PodBridgeConfig()));
}

public class AudioModeControllerTests
{
    private static FakeBackend Connected()
    {
        var b = new FakeBackend();
        b.Endpoints.AddRange([
            new("spk", "Speakers (Realtek(R) Audio)", Flow.Render, true),
            new("a2dp", "Headphones (AirPods Pro)", Flow.Render, true),
            new("hfp", "Headset (AirPods Pro Hands-Free AG Audio)", Flow.Render, true),
            new("mic", "Headset (AirPods Pro Hands-Free AG Audio)", Flow.Capture, true),
            new("lapmic", "Microphone Array (Realtek(R) Audio)", Flow.Capture, true),
        ]);
        foreach (var role in Enum.GetValues<Role>())
        {
            b.Defaults[(Flow.Render, role)] = "spk";
            b.Defaults[(Flow.Capture, role)] = "lapmic";
        }
        return b;
    }

    [Fact]
    public void Music_mode_uses_stereo_and_leaves_everyday_mic_alone()
    {
        var b = Connected();
        var c = new AudioModeController(b, () => new PodBridgeConfig(), _ => { });
        Assert.True(c.ApplyMusicMode());

        Assert.Equal("a2dp", b.Defaults[(Flow.Render, Role.Console)]);
        Assert.Equal("a2dp", b.Defaults[(Flow.Render, Role.Multimedia)]);
        Assert.Equal("hfp", b.Defaults[(Flow.Render, Role.Communications)]);
        Assert.Equal("mic", b.Defaults[(Flow.Capture, Role.Communications)]);
        Assert.Equal("lapmic", b.Defaults[(Flow.Capture, Role.Console)]);
    }

    [Fact]
    public void Call_mode_points_everything_at_headset_then_restores()
    {
        var b = Connected();
        var c = new AudioModeController(b, () => new PodBridgeConfig(), _ => { });
        c.ApplyMusicMode();
        c.ApplyCallMode();

        foreach (var role in Enum.GetValues<Role>())
        {
            Assert.Equal("hfp", b.Defaults[(Flow.Render, role)]);
            Assert.Equal("mic", b.Defaults[(Flow.Capture, role)]);
        }

        c.ApplyMusicMode();
        Assert.Equal("a2dp", b.Defaults[(Flow.Render, Role.Multimedia)]);
        Assert.Equal("lapmic", b.Defaults[(Flow.Capture, Role.Console)]);
        Assert.False(c.InCallMode);
    }

    [Fact]
    public void Does_nothing_when_airpods_disconnected()
    {
        var b = Connected();
        for (int i = 0; i < b.Endpoints.Count; i++)
            if (b.Endpoints[i].Name.Contains("AirPods")) b.Endpoints[i] = b.Endpoints[i] with { Active = false };
        var c = new AudioModeController(b, () => new PodBridgeConfig(), _ => { });
        Assert.False(c.ApplyCallMode());
        Assert.Equal("spk", b.Defaults[(Flow.Render, Role.Console)]);
    }
}

public class DefaultMicTests
{
    private static FakeBackend AirPodsMicIsDefault()
    {
        var b = new FakeBackend();
        b.Endpoints.AddRange([
            new("spk", "Speakers (Realtek(R) Audio)", Flow.Render, true),
            new("air", "Headphones (Alex’s AirPods Pro - Find My)", Flow.Render, true),
            new("airmic", "Headset (Alex’s AirPods Pro - Find My)", Flow.Capture, true),
            new("dock", "Microphone (USB-C Dock Audio)", Flow.Capture, false),
            new("array", "Microphone Array (Intel® Smart Sound Technology)", Flow.Capture, true),
        ]);
        foreach (var role in Enum.GetValues<Role>())
        {
            b.Defaults[(Flow.Render, role)] = "air";
            b.Defaults[(Flow.Capture, role)] = "airmic"; // what Windows did on the user's PC
        }
        return b;
    }

    [Fact]
    public void Music_mode_moves_everyday_mic_back_to_the_pc()
    {
        var b = AirPodsMicIsDefault();
        new AudioModeController(b, () => new PodBridgeConfig(), _ => { }).ApplyMusicMode();
        Assert.Equal("array", b.Defaults[(Flow.Capture, Role.Console)]);
        Assert.Equal("array", b.Defaults[(Flow.Capture, Role.Multimedia)]);
        Assert.Equal("airmic", b.Defaults[(Flow.Capture, Role.Communications)]);
    }

    [Fact]
    public void Call_then_music_round_trip()
    {
        var b = AirPodsMicIsDefault();
        var c = new AudioModeController(b, () => new PodBridgeConfig(), _ => { });
        c.ApplyMusicMode();
        c.ApplyCallMode();
        Assert.Equal("airmic", b.Defaults[(Flow.Capture, Role.Console)]);
        c.ApplyMusicMode();
        Assert.Equal("array", b.Defaults[(Flow.Capture, Role.Console)]);
    }

    [Fact]
    public void Can_be_turned_off()
    {
        var b = AirPodsMicIsDefault();
        new AudioModeController(b, () => new PodBridgeConfig { LaptopMicOutsideCalls = false }, _ => { }).ApplyMusicMode();
        Assert.Equal("airmic", b.Defaults[(Flow.Capture, Role.Console)]);
    }
}
