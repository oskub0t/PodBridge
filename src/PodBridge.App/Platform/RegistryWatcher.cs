using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PodBridge.App.Platform;

/// <summary>
/// Tells us when anything under a registry key changes (RegNotifyChangeKeyValue), so the mic-usage
/// record is read when Windows updates it instead of every second. Raises <see cref="Changed"/> on a
/// background thread. Per-user key, no admin.
/// </summary>
internal sealed class RegistryWatcher : IDisposable
{
    private const int RegNotifyChangeName = 0x1, RegNotifyChangeLastSet = 0x4;
    private readonly RegistryKey _key;
    private readonly AutoResetEvent _changed = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;

    public event Action? Changed;

    /// <exception cref="InvalidOperationException">The key doesn't exist.</exception>
    public RegistryWatcher(RegistryKey hive, string path)
    {
        _key = hive.OpenSubKey(path) ?? throw new InvalidOperationException($"Registry key not found: {path}");
        _thread = new Thread(Run) { IsBackground = true, Name = "PodBridge registry watcher" };
        _thread.Start();
    }

    private void Run()
    {
        var handles = new WaitHandle[] { _stop, _changed };
        while (true)
        {
            // One-shot: re-arm after every notification. The thread must stay alive while armed.
            int rc = RegNotifyChangeKeyValue(_key.Handle, true, RegNotifyChangeName | RegNotifyChangeLastSet,
                _changed.SafeWaitHandle, true);
            if (rc != 0) { Log.Write($"Registry watch failed ({rc}); falling back to polling"); return; }
            if (WaitHandle.WaitAny(handles) == 0) return;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
        _key.Dispose();
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree, int filter,
        SafeWaitHandle evt, bool asynchronous);
}
