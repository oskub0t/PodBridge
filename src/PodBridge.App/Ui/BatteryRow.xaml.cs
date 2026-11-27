using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using PodBridge.Core.Ble;

namespace PodBridge.App.Ui;

public partial class BatteryRow : UserControl
{
    public BatteryRow() => InitializeComponent();

    /// <param name="s">Per-part battery from the advertisement, if recent.</param>
    /// <param name="overall">Windows' own single battery value, used when there's no advert data.</param>
    public void Update(AirPodsStatus? s, int? overall = null)
    {
        bool hasParts = s is not null && (s.LeftPercent ?? s.RightPercent ?? s.CasePercent) is not null;
        Parts.Visibility = hasParts ? Visibility.Visible : Visibility.Collapsed;
        Overall.Visibility = !hasParts && overall is not null ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = !hasParts && overall is null ? Visibility.Visible : Visibility.Collapsed;
        OverallRing.Percent = overall;
        OverallPct.Text = overall is int o ? $"{o}%" : "";

        Set(LeftRing, LeftPct, LeftBolt, s?.LeftPercent, s?.LeftCharging == true);
        Set(RightRing, RightPct, RightBolt, s?.RightPercent, s?.RightCharging == true);
        Set(CaseRing, CasePct, CaseBolt, s?.CasePercent, s?.CaseCharging == true);
    }

    private static void Set(BatteryRing ring, TextBlock pct, Path bolt, int? value, bool charging)
    {
        ring.Percent = value;
        ring.Charging = charging;
        pct.Text = value is int v ? $"{v}%" : "–";
        bolt.Visibility = charging && value is not null ? Visibility.Visible : Visibility.Collapsed;
    }
}
