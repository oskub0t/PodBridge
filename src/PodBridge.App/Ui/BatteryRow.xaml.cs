using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using PodBridge.Core.Ble;

namespace PodBridge.App.Ui;

public partial class BatteryRow : UserControl
{
    public BatteryRow() => InitializeComponent();

    public void Update(AirPodsStatus? s)
    {
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
