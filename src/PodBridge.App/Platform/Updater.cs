using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using PodBridge.Core.Updates;

namespace PodBridge.App.Platform;

/// <summary>
/// Update check against GitHub Releases. This is PodBridge's only network use: one HTTPS request to
/// api.github.com per day (if enabled), plus the download when you choose Update. Uses Windows'
/// proxy settings. Updating swaps the installed exe after PodBridge exits, then restarts it.
/// </summary>
internal static class Updater
{
    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);

    private static string UpdateDir =>
        Path.Combine(Path.GetDirectoryName(AppPaths.LogFile)!, "update");

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"PodBridge/{CurrentVersion.ToString(3)}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static async Task<ReleaseInfo?> CheckAsync()
    {
        var json = await Http.GetStringAsync(ReleaseInfo.LatestReleaseApi);
        return ReleaseInfo.Parse(json);
    }

    /// <summary>
    /// Downloads the release next to PodBridge's log folder, checks its size and returns the exe.
    /// Prefers the zipped exe (less than half the download), falls back to the plain exe.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo r)
    {
        if (!r.HasDownload) throw new InvalidOperationException("This release has no PodBridge.exe attached.");
        Directory.CreateDirectory(UpdateDir);
        var exePath = Path.Combine(UpdateDir, $"PodBridge-{r.Version.ToString(3)}.exe");
        if (r.ZipUrl is not null)
        {
            var zipPath = Path.ChangeExtension(exePath, ".zip");
            await DownloadFileAsync(r.ZipUrl, zipPath, r.ZipSize);
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("PodBridge.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new IOException("The update zip has no PodBridge.exe in it.");
                entry.ExtractToFile(exePath, overwrite: true);
                if (new FileInfo(exePath).Length != entry.Length) throw new IOException("Unzipping the update failed.");
            }
            finally { File.Delete(zipPath); }
        }
        else await DownloadFileAsync(r.ExeUrl!, exePath, r.ExeSize);
        Log.Write($"Downloaded update {r.Tag} ({new FileInfo(exePath).Length} bytes)");
        return exePath;
    }

    private static async Task DownloadFileAsync(string url, string path, long expectedSize)
    {
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file);
        }
        var size = new FileInfo(path).Length;
        if (size < 1_000_000 || (expectedSize > 0 && size != expectedSize))
        {
            File.Delete(path);
            throw new IOException($"Download incomplete ({size} of {expectedSize} bytes).");
        }
    }

    /// <summary>Starts the downloaded exe in "apply update" mode. The caller then exits.</summary>
    public static void StartApply(string downloadedExe) =>
        Process.Start(new ProcessStartInfo(downloadedExe) { ArgumentList = { "--apply-update", Installer.InstalledExe } });

    /// <summary>
    /// Runs in the NEW exe: waits for the old PodBridge to exit, copies itself over the installed
    /// exe, refreshes the Apps entry, and starts the installed copy.
    /// </summary>
    public static void ApplyUpdate(string target)
    {
        SingleInstance.WaitForOtherToExit(TimeSpan.FromSeconds(20));
        for (int attempt = 0; ; attempt++)
        {
            try { File.Copy(Environment.ProcessPath!, target, overwrite: true); break; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(500); }
        }
        Installer.Register(target);
        Log.Write($"Update applied to {target}");
        Process.Start(new ProcessStartInfo(target) { ArgumentList = { "--wait-for-previous", "--updated" } });
    }

    /// <summary>Removes downloaded update files left over from earlier updates.</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(UpdateDir)) return;
            foreach (var f in Directory.GetFiles(UpdateDir, "*.exe").Concat(Directory.GetFiles(UpdateDir, "*.zip")))
                if (!string.Equals(f, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
    }
}

/// <summary>
/// One PodBridge per user session, plus a way for the installer/updater to ask a running copy to
/// quit (named event) and wait until it has.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\PodBridge.SingleInstance";
    private const string QuitEventName = @"Local\PodBridge.Quit";
    private static Mutex? _mutex;

    /// <returns>True if this process is now the one PodBridge.</returns>
    public static bool TryAcquire(TimeSpan wait)
    {
        _mutex = new Mutex(false, MutexName);
        try { return _mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; }
    }

    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex?.Dispose();
        _mutex = null;
    }

    /// <summary>Calls <paramref name="onQuit"/> when another process asks this one to quit.</summary>
    public static void ListenForQuit(Action onQuit)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        var t = new Thread(() => { ev.WaitOne(); onQuit(); }) { IsBackground = true, Name = "PodBridge quit listener" };
        t.Start();
    }

    /// <summary>Asks a running PodBridge to quit and waits until it has released the mutex.</summary>
    public static void WaitForOtherToExit(TimeSpan timeout)
    {
        if (EventWaitHandle.TryOpenExisting(QuitEventName, out var ev)) { ev.Set(); ev.Dispose(); }
        using var m = new Mutex(false, MutexName);
        try { if (m.WaitOne(timeout)) m.ReleaseMutex(); }
        catch (AbandonedMutexException) { m.ReleaseMutex(); }
    }
}
