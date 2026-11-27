using PodBridge.Core.Ble;

namespace PodBridge.Core.Tests;

public class ParserTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    // type, len(25), prefix, model hi, model lo, status, pods, flags|case, lid, then 18 opaque bytes
    private static byte[] Advert(ushort model, byte status, byte pods, byte flagsCase)
    {
        var b = new byte[27];
        b[0] = 0x07; b[1] = 25; b[2] = 0x01;
        b[3] = (byte)(model >> 8); b[4] = (byte)model;
        b[5] = status; b[6] = pods; b[7] = flagsCase; b[8] = 0x01;
        return b;
    }

    [Fact]
    public void Parses_model_battery_and_in_ear_unflipped()
    {
        // status 0x2B: 0x20 set → not flipped; low nibble 1011 → left (0x02) and right (0x08) in ear
        // pods 0x78: not flipped → left = low nibble 8, right = high nibble 7
        // flags/case 0x46: charge nibble 0100 → case charging; case battery 6
        var s = ProximityPairingParser.TryParse(Advert(0x0E20, 0x2B, 0x78, 0x46), -50, T0)!;

        Assert.Equal(0x0E20, s.ModelId);
        Assert.Equal(80, s.LeftPercent);
        Assert.Equal(70, s.RightPercent);
        Assert.Equal(60, s.CasePercent);
        Assert.True(s.CaseCharging);
        Assert.False(s.LeftCharging);
        Assert.True(s.LeftInEar);
        Assert.True(s.RightInEar);
        Assert.Equal(70, s.LowestPodPercent);
    }

    [Fact]
    public void Flipped_orientation_swaps_left_and_right()
    {
        // status 0x02: 0x20 clear → flipped. Flipped: left in-ear bit is 0x08, right is 0x02.
        // pods 0x78 flipped → left = high 7, right = low 8. charge 0001 flipped → right charging.
        var s = ProximityPairingParser.TryParse(Advert(0x1420, 0x02, 0x78, 0x1A), -50, T0)!;

        Assert.Equal(70, s.LeftPercent);
        Assert.Equal(80, s.RightPercent);
        Assert.Equal(100, s.CasePercent);
        Assert.True(s.RightCharging);
        Assert.False(s.LeftCharging);
        Assert.False(s.LeftInEar);
        Assert.True(s.RightInEar);
    }

    [Fact]
    public void Nibble_15_means_unknown()
    {
        var s = ProximityPairingParser.TryParse(Advert(0x1420, 0x20, 0xF5, 0x0F), -50, T0)!;
        Assert.Equal(50, s.LeftPercent);
        Assert.Null(s.RightPercent);
        Assert.Null(s.CasePercent);
        Assert.Equal(50, s.LowestPodPercent);
    }

    [Fact]
    public void Skips_other_apple_tlvs_before_proximity_pairing()
    {
        var nearby = new byte[] { 0x10, 0x03, 0xAA, 0xBB, 0xCC };
        var data = nearby.Concat(Advert(0x2420, 0x20, 0x55, 0x05)).ToArray();
        var s = ProximityPairingParser.TryParse(data, -40, T0);
        Assert.NotNull(s);
        Assert.Equal(0x2420, s!.ModelId);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x07 })]
    [InlineData(new byte[] { 0x07, 0x19, 0x01, 0x0E })]           // truncated
    [InlineData(new byte[] { 0x10, 0x05, 0x01, 0x02, 0x03, 0x04, 0x05 })] // iPhone "nearby", not AirPods
    public void Rejects_garbage(byte[] data) =>
        Assert.Null(ProximityPairingParser.TryParse(data, -40, T0));

    [Theory]
    [InlineData((ushort)0x2720, "AirPods Pro 3")]
    [InlineData((ushort)0x3620, "AirPods 5")]
    [InlineData((ushort)0x1920, "AirPods 4")]
    [InlineData((ushort)0x1420, "AirPods Pro 2")]
    [InlineData((ushort)0x2420, "AirPods Pro 2 (USB-C)")]
    [InlineData((ushort)0x1F20, "AirPods Max (USB-C)")]
    public void Known_models_are_named(ushort id, string name)
    {
        Assert.True(AirPodsModels.IsKnown(id));
        Assert.Equal(name, AirPodsModels.NameFor(id));
    }

    [Fact]
    public void Unknown_model_gets_readable_name_and_config_override_wins()
    {
        Assert.Equal("Apple audio device 0x4220", AirPodsModels.NameFor(0x4220));
        var extra = new Dictionary<ushort, string> { [0x4220] = "My AirPods" };
        Assert.Equal("My AirPods", AirPodsModels.NameFor(0x4220, extra));
    }

    [Fact]
    public void Config_parses_hex_model_names()
    {
        var c = new PodBridgeConfig { ModelNames = { ["0x2720"] = "AirPods Pro 3", ["bogus"] = "x" } };
        var parsed = c.ParsedModelNames();
        Assert.Single(parsed);
        Assert.Equal("AirPods Pro 3", parsed[0x2720]);
    }
}
