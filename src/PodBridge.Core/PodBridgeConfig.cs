using System.Text.Json.Serialization;
using PodBridge.Core.Features;

namespace PodBridge.Core;

public sealed class PodBridgeConfig
{
    /// <summary>BLE model ID of *your* AirPods. Set by "Learn my AirPods". Null = not learned yet.</summary>
    public ushort? MyModelId { get; set; }

    /// <summary>
    /// The Bluetooth name Windows shows, e.g. "AirPods Pro" or "Sam's AirPods Pro". Used to find the
    /// audio endpoints. Null = auto-detect (first endpoint whose name contains "AirPods").
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>Ignore adverts weaker than this. -70 dBm ≈ same desk. Raise to -60 if a neighbour's
    /// identical AirPods still trigger it.</summary>
    public short MinRssi { get; set; } = -70;

    /// <summary>Seconds without adverts before the next one counts as "case opened" again.</summary>
    public int AbsenceSeconds { get; set; } = 30;

    /// <summary>Connect automatically at all ("Never" in Settings turns this off).</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>When automatic connecting happens: when a bud goes in (default) or when the case opens.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ConnectTrigger>))]
    public ConnectTrigger ConnectWhen { get; set; } = ConnectTrigger.PutInEar;
    public bool ShowPopup { get; set; } = true;
    public bool AutoCallMode { get; set; } = true;

    /// <summary>Pause when an AirPod comes out, resume when it goes back in.</summary>
    public bool EarDetection { get; set; } = true;

    /// <summary>What to do when you're wearing your AirPods elsewhere and this PC starts playing.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<SwitchPolicy>))]
    public SwitchPolicy ConnectToThisPc { get; set; } = SwitchPolicy.Ask;

    public bool LowBatteryAlerts { get; set; } = true;

    /// <summary>
    /// Outside calls, keep the PC's own mic as the default recording device so apps that open the
    /// default mic don't drop the AirPods to mono. Calls still switch to the AirPods mic.
    /// </summary>
    public bool LaptopMicOutsideCalls { get; set; } = true;

    /// <summary>
    /// Advanced features (noise control, Conversation Awareness, exact battery, instant ear
    /// detection). OFF by default, and only usable once the separately installed PodBridge driver
    /// is present, which needs administrator rights to install. Without it PodBridge runs the
    /// no-admin features only.
    /// </summary>
    public bool AdvancedFeatures { get; set; }

    /// <summary>Windows 11 Acrylic/Mica behind cards. Turn off if your PC renders them oddly.</summary>
    public bool UseSystemMaterials { get; set; } = true;

    /// <summary>
    /// Substrings matched against the microphone-usage keys Windows keeps per app. New Teams is the
    /// packaged app "MSTeams_8wekyb3d8bbwe"; classic Teams shows up as a path ending in Teams.exe.
    /// </summary>
    public List<string> CallApps { get; set; } = ["MSTeams", "Teams.exe", "Zoom.exe", "slack", "Webex"];

    /// <summary>Warn if the AirPods mic stays silent this long during a call (0 = off).</summary>
    public int SilentMicWarningSeconds { get; set; } = 20;

    /// <summary>Optional exact endpoint names, if auto-detection picks the wrong ones.</summary>
    public string? MusicOutputName { get; set; }
    public string? CallOutputName { get; set; }
    public string? MicName { get; set; }

    /// <summary>Extra model names, e.g. { "0x2720": "AirPods Pro 3" } once you have learned it.</summary>
    public Dictionary<string, string> ModelNames { get; set; } = [];

    public IReadOnlyDictionary<ushort, string> ParsedModelNames() =>
        ModelNames
            .Select(kv => (ok: TryParseHex(kv.Key, out var id), id, kv.Value))
            .Where(x => x.ok)
            .ToDictionary(x => x.id, x => x.Value);

    private static bool TryParseHex(string s, out ushort id)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out id);
    }
}
