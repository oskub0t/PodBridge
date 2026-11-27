using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using PodBridge.Core;
using PodBridge.Core.Calls;

namespace PodBridge.App.Platform;

/// <summary>
/// Reads Windows' own "which app is using the microphone" record (the same data behind the mic
/// icon in the taskbar). Per-user registry, readable without admin. LastUsedTimeStop == 0 means the
/// app is holding the mic right now.
/// </summary>
internal sealed class RegistryMicUsageSource : IMicUsageSource
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public IReadOnlyList<MicUsageEntry> Read()
    {
        var list = new List<MicUsageEntry>();
        using var root = Registry.CurrentUser.OpenSubKey(Root);
        if (root is null) return list;

        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key is null) continue;
            if (name == "NonPackaged")
            {
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var k = key.OpenSubKey(sub);
                    if (k is not null) list.Add(new MicUsageEntry(sub, InUse(k)));
                }
            }
            else list.Add(new MicUsageEntry(name, InUse(key)));
        }
        return list;
    }

    private static bool InUse(RegistryKey k) =>
        k.GetValue("LastUsedTimeStart") is long start && start != 0 &&
        k.GetValue("LastUsedTimeStop") is long stop && stop == 0;
}

/// <summary>Start-with-Windows via HKCU\...\Run (per user, no admin).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PodBridge";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled, string? exe = null)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(Name, $"\"{exe ?? Environment.ProcessPath}\" --background");
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }
}

internal static class AppPaths
{
    public static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PodBridge");
    public static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");
    public static readonly string LogFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PodBridge", "log.txt");
}

internal static class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static PodBridgeConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
                return JsonSerializer.Deserialize<PodBridgeConfig>(File.ReadAllText(AppPaths.ConfigFile), Json) ?? new();
        }
        catch (Exception ex) { Log.Write($"Config unreadable, using defaults: {ex.Message}"); }
        return new PodBridgeConfig();
    }

    public static void Save(PodBridgeConfig c)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(c, Json));
    }
}

/// <summary>Last known battery levels, so they survive restarts. Per user, local only.</summary>
internal static class BatteryStore
{
    private static readonly string File_ =
        Path.Combine(Path.GetDirectoryName(AppPaths.LogFile)!, "battery.json");

    public static PodBridge.Core.Ble.BatteryMemory Load()
    {
        try
        {
            if (File.Exists(File_))
                return JsonSerializer.Deserialize<PodBridge.Core.Ble.BatteryMemory>(File.ReadAllText(File_)) ?? new();
        }
        catch (Exception ex) { Log.Write($"Battery memory unreadable, starting fresh: {ex.Message}"); }
        return new();
    }

    public static void Save(PodBridge.Core.Ble.BatteryMemory m)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(m));
        }
        catch (IOException ex) { Log.Write($"Couldn't save battery memory: {ex.Message}"); }
    }
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LogFile)!);
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch (IOException) { }
    }
}
