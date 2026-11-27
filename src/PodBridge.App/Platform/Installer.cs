using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PodBridge.App.Platform;

/// <summary>
/// Per-user install, no admin: copies the exe to %LOCALAPPDATA%\Programs\PodBridge, adds a Start
/// menu shortcut and a Settings → Apps entry (HKCU Uninstall key) with a working uninstaller.
/// The same layout Windows uses for per-user apps such as VS Code's user installer.
/// </summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PodBridge";
    public const string RepoUrl = "https://github.com/oskub0t/PodBridge";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PodBridge");

    public static string InstalledExe => Path.Combine(InstallDir, "PodBridge.exe");

    private static string StartShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "PodBridge.lnk");

    public static bool IsInstalled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return k is not null && File.Exists(InstalledExe);
        }
    }

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies this exe into place and registers it. Returns the installed exe path.</summary>
    public static string Install()
    {
        Directory.CreateDirectory(InstallDir);
        if (!IsRunningInstalled) File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);
        Register(InstalledExe);
        if (Autostart.IsEnabled) Autostart.Set(true, InstalledExe);
        Log.Write($"Installed to {InstalledExe}");
        return InstalledExe;
    }

    /// <summary>(Re)writes the Start shortcut and the Apps entry, e.g. after an update.</summary>
    public static void Register(string exe)
    {
        CreateShortcut(StartShortcut, exe, "AirPods for Windows");
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        var version = typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "0";
        k.SetValue("DisplayName", "PodBridge");
        k.SetValue("DisplayVersion", version);
        k.SetValue("Publisher", "PodBridge (open source)");
        k.SetValue("DisplayIcon", $"\"{exe}\",0");
        k.SetValue("InstallLocation", Path.GetDirectoryName(exe)!);
        k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        k.SetValue("URLInfoAbout", RepoUrl);
        k.SetValue("HelpLink", RepoUrl + "/issues");
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>Removes everything the install created. Settings are removed only if asked.</summary>
    public static void Uninstall(bool removeSettings)
    {
        Autostart.Set(false);
        Notifications.Unregister();
        TryDelete(StartShortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        if (removeSettings)
        {
            TryDeleteDir(AppPaths.ConfigDir);
            TryDeleteDir(Path.GetDirectoryName(AppPaths.LogFile)!);
        }
        // The running exe can't delete itself: remove the install folder shortly after we exit.
        if (Directory.Exists(InstallDir))
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        Log.Write("Uninstalled");
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch (IOException) { } }

    private static void TryDeleteDir(string dir) { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    // ---- .lnk creation via the shell's IShellLink (no admin, no dependencies) ----

    private static void CreateShortcut(string lnkPath, string target, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
        link.SetDescription(description);
        link.SetIconLocation(target, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        ((IPersistFile)link).Save(lnkPath, true);
        Marshal.FinalReleaseComObject(link);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr a, int b, IntPtr c, int d);
        void GetIDList(out IntPtr a);
        void SetIDList(IntPtr a);
        void GetDescription(IntPtr a, int b);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr a, int b);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr a, int b);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short a);
        void SetHotkey(short a);
        void GetShowCmd(out int a);
        void SetShowCmd(int a);
        void GetIconLocation(IntPtr a, int b, out int c);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile(out IntPtr file);
    }
}
