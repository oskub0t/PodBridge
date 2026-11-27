using PodBridge.Core.Ble;
using PodBridge.Core.Features;

namespace PodBridge.Core.Tests;

/// <summary>Sequences from the user's 0.3.10 log (AirPods Pro 3).</summary>
public class EarStateMergerTests
{
    private static AirPodsStatus Advert(byte status, byte lid, bool leftCharging, bool rightCharging)
    {
        // Real advert bytes; patch status, lid and the charging nibble (0x8? = none, 0x1? / 0x2? per pod).
        var bytes = Convert.FromHexString("07190127203388A61700057B41C7247934D8D25E1EA2C15436B297");
        bytes[5] = status;
        bool flip = (status & 0x20) == 0;
        int charge = (leftCharging ? (flip ? 0b010 : 0b001) : 0) | (rightCharging ? (flip ? 0b001 : 0b010) : 0);
        bytes[7] = (byte)((charge << 4) | 0x0F);
        bytes[8] = lid;
        return ProximityPairingParser.TryParse(bytes, -46, DateTimeOffset.UnixEpoch)!;
    }

    [Fact]
    public void Left_in_ear_right_charging_in_case_stays_one()
    {
        var m = new EarStateMerger();
        var counts = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            counts.Add(m.Observe(Advert(0x33, 0x17, false, true)));  // left pod, out of case
            counts.Add(m.Observe(Advert(0x53, 0x37, false, true)));  // right pod, in the case
        }
        Assert.All(counts, c => Assert.Equal(1, c));
    }

    [Fact]
    public void Transitional_advert_from_a_charging_pod_does_not_drop_the_count()
    {
        // 16.28.03: the right pod sent 0x53 with lid 0x17 while charging. Charging = in the case.
        var m = new EarStateMerger();
        Assert.Equal(1, m.Observe(Advert(0x33, 0x17, false, true)));
        Assert.Equal(1, m.Observe(Advert(0x53, 0x17, false, true)));
        Assert.Equal(1, m.Observe(Advert(0x53, 0x38, false, true)));  // lid closed
    }

    [Fact]
    public void Both_worn_both_in_case()
    {
        var m = new EarStateMerger();
        Assert.Equal(2, m.Observe(Advert(0x0B, 0x13, false, false)));
        Assert.Equal(2, m.Observe(Advert(0x2B, 0x11, false, false)));
        Assert.Equal(1, m.Observe(Advert(0x09, 0x17, false, false)));   // right out
        var c = new EarStateMerger();
        c.Observe(Advert(0x15, 0x33, true, true));
        Assert.Equal(0, c.Observe(Advert(0x75, 0x33, true, true)));
    }

    [Fact]
    public void Pod_in_case_only_speaks_for_itself()
    {
        var m = new EarStateMerger();
        m.Observe(Advert(0x2B, 0x11, false, false));                     // both worn
        Assert.Equal(1, m.Observe(Advert(0x53, 0x37, false, true)));      // right went into the case
    }
}
