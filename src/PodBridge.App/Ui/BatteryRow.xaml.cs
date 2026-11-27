using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using PodBridge.Core.Ble;

namespace PodBridge.App.Ui;

public partial class BatteryRow : UserControl
{
    private const double StaleOpacity = 0.5;

    public BatteryRow() => InitializeComponent();

    /// <param name="v">Per-part battery: live values plus remembered ones (with their age).</param>
    /// <param name="overall">Windows' own single battery value, used when nothing else is known.</param>
    public void Update(BatteryView? v, int? overall = null)
    {
        bool hasParts = v?.HasAny == true;
        Parts.Visibility = hasParts ? Visibility.Visible : Visibility.Collapsed;
        Overall.Visibility = !hasParts && overall is not null ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = !hasParts && overall is null ? Visibility.Visible : Visibility.Collapsed;
        OverallRing.Percent = overall;
        OverallPct.Text = overall is int o ? $"{o}%" : "";
        AutomationProperties.SetName(OverallRing, overall is int o2 ? $"AirPods battery {o2} percent" : "AirPods battery unknown");

        Set(LeftRing, LeftPct, LeftBolt, LeftCaption, v?.Left ?? PartView.Unknown, "Left", "Left AirPod");
        Set(RightRing, RightPct, RightBolt, RightCaption, v?.Right ?? PartView.Unknown, "Right", "Right AirPod");
        Set(CaseRing, CasePct, CaseBolt, CaseCaption, v?.Case ?? PartView.Unknown, "Case", "Case");
    }

    private static void Set(BatteryRing ring, TextBlock pct, Path bolt, TextBlock caption, PartView part, string shortLabel, string label)
    {
        int? value = part.Percent;
        string? age = part.Age is TimeSpan a ? BatteryView.FormatAge(a) : null;

        ring.Percent = value;
        ring.Charging = part.Charging;
        pct.Text = value is int v ? $"{v}%" : "–";
        bolt.Visibility = part.Charging && value is not null ? Visibility.Visible : Visibility.Collapsed;

        // Remembered values are dimmed and say how old they are, so nobody mistakes them for live.
        double opacity = age is null ? 1 : StaleOpacity;
        ring.Opacity = pct.Opacity = opacity;
        caption.Text = age is null ? shortLabel : $"{shortLabel}\n{age}";

        // Screen readers: "Case, 60 percent, last updated 2 h ago" / "Left AirPod, 80 percent, charging".
        AutomationProperties.SetName(ring, value is int p
            ? $"{label}, {p} percent{(part.Charging ? ", charging" : "")}{(age is null ? "" : $", last updated {age}")}"
            : $"{label}, battery unknown");
    }
}
