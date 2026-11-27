using System.Text.RegularExpressions;

namespace PodBridge.Core.Audio;

/// <summary>
/// AirPods on Windows usually show up as two output endpoints and one input:
///   "Headphones (AirPods Pro)"                     – A2DP stereo, good quality, no mic
///   "Headset (AirPods Pro Hands-Free AG Audio)"    – HFP mono, has the mic (Win10 naming)
///   "Headset (AirPods Pro Hands-Free)" / "Headset (AirPods Pro)" – newer naming
/// Recent Windows 11 builds can merge the outputs into one endpoint that switches by itself; then
/// CallOutput is null and the music output is used for both.
///
/// The classic "people can't hear me / I can't hear them" setup: Teams speaker = "Headphones",
/// Teams mic = AirPods. Once the mic opens the link drops to HFP and the stereo endpoint goes quiet.
/// </summary>
public sealed record ResolvedEndpoints(
    string DeviceName,
    AudioEndpoint? MusicOutput,
    AudioEndpoint? CallOutput,
    AudioEndpoint? Mic)
{
    public bool AnyActive => MusicOutput?.Active == true || CallOutput?.Active == true;
    public AudioEndpoint? CallOutputOrMusic => CallOutput ?? MusicOutput;
}

public static partial class EndpointResolver
{
    [GeneratedRegex(@"\((?<inner>.+)\)\s*$")]
    private static partial Regex ParenRegex();

    [GeneratedRegex(@"\s+(Hands-Free( AG Audio)?|Stereo)$", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixRegex();

    /// <summary>"Headset (Sam’s AirPods Pro Hands-Free AG Audio)" → "Sam’s AirPods Pro".</summary>
    public static string BaseDeviceName(string endpointName)
    {
        var m = ParenRegex().Match(endpointName);
        var inner = m.Success ? m.Groups["inner"].Value : endpointName;
        return SuffixRegex().Replace(inner.Trim(), "").Trim();
    }

    /// <summary>Distinct device names of AirPods-looking endpoints (for auto-detect / diagnostics).</summary>
    public static IReadOnlyList<string> FindAirPodsDevices(IEnumerable<AudioEndpoint> endpoints) =>
        endpoints
            .Where(e => e.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase))
            .Select(e => BaseDeviceName(e.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static ResolvedEndpoints? Resolve(IReadOnlyList<AudioEndpoint> endpoints, PodBridgeConfig config)
    {
        var device = config.DeviceName;
        if (string.IsNullOrWhiteSpace(device))
        {
            var found = FindAirPodsDevices(endpoints);
            if (found.Count == 0) return null;
            device = found[0];
        }

        bool BelongsToDevice(AudioEndpoint e) =>
            BaseDeviceName(e.Name).Equals(device, StringComparison.OrdinalIgnoreCase);

        AudioEndpoint? ByExactName(string? name, Flow flow) =>
            name is null ? null : endpoints.FirstOrDefault(e => e.Flow == flow &&
                e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        var mine = endpoints.Where(BelongsToDevice).ToList();
        var renders = mine.Where(e => e.Flow == Flow.Render).ToList();

        static bool IsHandsFree(AudioEndpoint e) =>
            e.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ||
            e.Name.StartsWith("Headset", StringComparison.OrdinalIgnoreCase);

        var music = ByExactName(config.MusicOutputName, Flow.Render)
                    ?? renders.FirstOrDefault(e => !IsHandsFree(e))
                    ?? (renders.Count == 1 ? renders[0] : null);
        var call = ByExactName(config.CallOutputName, Flow.Render)
                   ?? renders.FirstOrDefault(e => IsHandsFree(e) && e != music);
        var mic = ByExactName(config.MicName, Flow.Capture)
                  ?? mine.FirstOrDefault(e => e.Flow == Flow.Capture);

        if (music is null && call is null && mic is null) return null;
        return new ResolvedEndpoints(device, music, call, mic);
    }
}
