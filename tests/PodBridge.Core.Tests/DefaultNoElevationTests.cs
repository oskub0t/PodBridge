namespace PodBridge.Core.Tests;

/// <summary>
/// Guards the promise that PodBridge.exe never asks for administrator rights. Advanced features
/// need a separately installed driver; the app itself must keep running as the normal user.
/// </summary>
public class DefaultNoElevationTests
{
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PodBridge.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    [Fact]
    public void App_manifest_runs_as_invoker()
    {
        var manifest = File.ReadAllText(RepoFile("src/PodBridge.App/app.manifest"));
        Assert.Contains("level=\"asInvoker\"", manifest);
        Assert.DoesNotContain("requireAdministrator", manifest);
        Assert.DoesNotContain("highestAvailable", manifest);
    }

    [Fact]
    public void Advanced_features_default_off() => Assert.False(new PodBridgeConfig().AdvancedFeatures);
}
