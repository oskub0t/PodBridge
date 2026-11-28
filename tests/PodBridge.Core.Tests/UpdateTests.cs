using PodBridge.Core.Updates;

namespace PodBridge.Core.Tests;

public class ReleaseInfoTests
{
    private const string Json = """
    {
      "tag_name": "v0.4.0",
      "html_url": "https://github.com/oskub0t/PodBridge/releases/tag/v0.4.0",
      "draft": false, "prerelease": false,
      "assets": [
        { "name": "notes.txt", "browser_download_url": "https://example.invalid/notes.txt", "size": 10 },
        { "name": "PodBridge.exe", "browser_download_url": "https://github.com/oskub0t/PodBridge/releases/download/v0.4.0/PodBridge.exe", "size": 81777419 }
      ]
    }
    """;

    [Fact]
    public void Parses_github_latest_release()
    {
        var r = ReleaseInfo.Parse(Json)!;
        Assert.Equal(new Version(0, 4, 0, 0), r.Version);
        Assert.EndsWith("/v0.4.0/PodBridge.exe", r.ExeUrl);
        Assert.Equal(81777419, r.ExeSize);
        Assert.True(r.IsNewerThan(new Version(0, 3, 11, 0)));
        Assert.False(r.IsNewerThan(new Version(0, 4, 0, 0)));
        Assert.False(r.IsNewerThan(new Version(0, 4, 1)));
    }

    [Fact]
    public void Reads_both_the_exe_and_the_zipped_exe()
    {
        var r = ReleaseInfo.Parse("""
        { "tag_name": "v0.4.8", "assets": [
          { "name": "PodBridge.exe", "browser_download_url": "https://x.invalid/PodBridge.exe", "size": 201318444 },
          { "name": "PodBridge.zip", "browser_download_url": "https://x.invalid/PodBridge.zip", "size": 78000000 } ] }
        """)!;
        Assert.Equal("https://x.invalid/PodBridge.exe", r.ExeUrl); // older PodBridge versions use this
        Assert.Equal("https://x.invalid/PodBridge.zip", r.ZipUrl);
        Assert.Equal(78000000, r.ZipSize);
        Assert.True(r.HasDownload);
        Assert.False(ReleaseInfo.Parse("""{"tag_name":"0.6"}""")!.HasDownload);
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.5.0","draft":true}""")]
    [InlineData("""{"tag_name":"v0.5.0","prerelease":true}""")]
    [InlineData("""{"tag_name":"latest"}""")]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("not json")]
    public void Ignores_unusable_responses(string json) => Assert.Null(ReleaseInfo.Parse(json));

    [Fact]
    public void Release_without_exe_still_offers_the_page()
    {
        var r = ReleaseInfo.Parse("""{"tag_name":"0.6","html_url":"https://x.invalid/r"}""")!;
        Assert.Null(r.ExeUrl);
        Assert.Equal(new Version(0, 6, 0, 0), r.Version);
    }

    [Theory]
    [InlineData("v0.3.11", 0, 3, 11)]
    [InlineData("0.3.11+5150b90", 0, 3, 11)]
    [InlineData("1", 1, 0, 0)]
    public void Versions_parse(string text, int a, int b, int c) =>
        Assert.Equal(new Version(a, b, c, 0), ReleaseInfo.ParseVersion(text));

    [Fact]
    public void Two_digit_parts_compare_numerically() =>
        Assert.True(ReleaseInfo.ParseVersion("0.3.11") > ReleaseInfo.ParseVersion("0.3.9"));
}

public class UpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Daily_and_respects_the_switch()
    {
        Assert.True(UpdatePolicy.ShouldCheck(true, null, Now));
        Assert.False(UpdatePolicy.ShouldCheck(true, Now.AddHours(-3), Now));
        Assert.True(UpdatePolicy.ShouldCheck(true, Now.AddHours(-25), Now));
        Assert.False(UpdatePolicy.ShouldCheck(false, null, Now));
        Assert.True(UpdatePolicy.ShouldCheck(true, Now.AddDays(2), Now)); // clock went backwards
    }
}
