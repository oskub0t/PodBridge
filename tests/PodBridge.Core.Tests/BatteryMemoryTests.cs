using System.Text.Json;
using PodBridge.Core.Ble;

namespace PodBridge.Core.Tests;

public class BatteryMemoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private static AirPodsStatus S(int? l, int? r, int? c, DateTimeOffset t, bool caseCharging = false, ushort model = 0x2720) =>
        new(model, l, r, c, false, false, caseCharging, false, false, -50, t);

    [Fact]
    public void Live_values_are_marked_live()
    {
        var m = new BatteryMemory();
        m.Observe(S(80, 70, 60, T0));
        var v = m.View(T0.AddSeconds(5));
        Assert.True(v.Left.IsLive && v.Case.IsLive);
        Assert.Equal(60, v.Case.Percent);
        Assert.Null(v.OldestAge);
    }

    [Fact]
    public void Closing_the_case_keeps_its_last_level()
    {
        var m = new BatteryMemory();
        m.Observe(S(80, 70, 60, T0));                 // lid open
        m.Observe(S(80, 70, null, T0.AddMinutes(30))); // pods in ears, case unknown
        var v = m.View(T0.AddMinutes(30));
        Assert.Equal(60, v.Case.Percent);
        Assert.False(v.Case.IsLive);
        Assert.Equal(TimeSpan.FromMinutes(30), v.Case.Age);
        Assert.True(v.Left.IsLive);
    }

    [Fact]
    public void Everything_is_kept_when_adverts_stop()
    {
        var m = new BatteryMemory();
        m.Observe(S(80, 70, 60, T0));
        var v = m.View(T0.AddHours(3));
        Assert.Equal(80, v.Left.Percent);
        Assert.Equal(TimeSpan.FromHours(3), v.OldestAge);
    }

    [Fact]
    public void Stale_values_never_claim_to_be_charging()
    {
        var m = new BatteryMemory();
        m.Observe(S(80, 70, 40, T0, caseCharging: true));
        Assert.True(m.View(T0).Case.Charging);
        Assert.False(m.View(T0.AddHours(2)).Case.Charging);
    }

    [Fact]
    public void Different_airpods_start_fresh()
    {
        var m = new BatteryMemory();
        m.Observe(S(80, 70, 60, T0));
        m.Observe(S(50, null, null, T0.AddMinutes(1), model: 0x1420));
        var v = m.View(T0.AddMinutes(1));
        Assert.Equal(50, v.Left.Percent);
        Assert.Null(v.Right.Percent);
        Assert.Null(v.Case.Percent);
    }

    [Fact]
    public void Reports_changes_only_when_worth_saving()
    {
        var m = new BatteryMemory();
        Assert.True(m.Observe(S(80, 70, 60, T0)));
        Assert.False(m.Observe(S(80, 70, null, T0.AddSeconds(1))));
        Assert.True(m.Observe(S(70, 70, null, T0.AddMinutes(20))));
    }

    [Fact]
    public void Survives_a_restart_via_json()
    {
        var m = new BatteryMemory();
        m.Observe(S(80, 70, 60, T0));
        var back = JsonSerializer.Deserialize<BatteryMemory>(JsonSerializer.Serialize(m))!;
        Assert.Equal((ushort?)0x2720, back.ModelId);
        Assert.Equal(60, back.View(T0.AddHours(1)).Case.Percent);
    }

    [Theory]
    [InlineData(20, "just now")]
    [InlineData(60 * 12, "12 min ago")]
    [InlineData(60 * 60 * 3, "3 h ago")]
    [InlineData(60 * 60 * 30, "yesterday")]
    [InlineData(60 * 60 * 24 * 4, "4 days ago")]
    public void Ages_read_naturally(int seconds, string text) =>
        Assert.Equal(text, BatteryView.FormatAge(TimeSpan.FromSeconds(seconds)));
}
