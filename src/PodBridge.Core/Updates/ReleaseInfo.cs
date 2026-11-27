using System.Text.Json;

namespace PodBridge.Core.Updates;

/// <summary>A published PodBridge release, read from GitHub's "latest release" API response.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string? ExeUrl, long ExeSize)
{
    public const string LatestReleaseApi = "https://api.github.com/repos/oskub0t/PodBridge/releases/latest";
    public const string ReleasesPage = "https://github.com/oskub0t/PodBridge/releases/latest";

    /// <returns>Null if the JSON isn't a usable, non-draft, non-prerelease release.</returns>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
            var tag = root.GetProperty("tag_name").GetString();
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            if (tag is null || ParseVersion(tag) is not Version v) return null;

            string? exe = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), "PodBridge.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exe = a.GetProperty("browser_download_url").GetString();
                        size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                    }
            return new ReleaseInfo(v, tag, page ?? ReleasesPage, exe, size);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"v0.4.0", "0.4", "0.4.0.0" → comparable Version (missing parts = 0).</summary>
    public static Version? ParseVersion(string text)
    {
        var t = text.Trim().TrimStart('v', 'V');
        int plus = t.IndexOfAny(['+', '-', ' ']);
        if (plus >= 0) t = t[..plus];
        if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }

    public bool IsNewerThan(Version current) =>
        Version > new Version(current.Major, current.Minor, Math.Max(0, current.Build), Math.Max(0, current.Revision));
}

/// <summary>When to ask GitHub: at most once a day, and only if the user hasn't turned it off.</summary>
public static class UpdatePolicy
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static bool ShouldCheck(bool enabled, DateTimeOffset? lastCheck, DateTimeOffset now) =>
        enabled && (lastCheck is null || now - lastCheck >= Interval || lastCheck > now);
}
