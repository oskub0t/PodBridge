using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PodBridge.Core.Aap;

namespace PodBridge.App.Platform;

/// <summary>
/// Talks to the OPTIONAL PodBridge AAP driver (driver/, installed separately with admin; see
/// docs/advanced-features.md). Contract with the driver:
///   - it registers device interface <see cref="InterfaceGuid"/> for the AirPods' AAP device node;
///   - opening the interface opens the L2CAP channel (PSM 0x1001), closing it closes the channel;
///   - each ReadFile returns exactly one AAP packet, each WriteFile sends one;
///   - I/O fails once the AirPods disconnect.
/// Opening the interface needs no admin. If no driver is installed, nothing here is ever used.
/// </summary>
internal sealed class AapDriverTransport : IAapTransport
{
    public static readonly Guid InterfaceGuid = new("14937a53-ba6b-4b1f-ba4b-2e3ebe1c57cb");

    private readonly FileStream _stream;
    private readonly CancellationTokenSource _cts = new();

    public event Action<byte[]>? Received;
    public event Action? Closed;

    private AapDriverTransport(FileStream stream)
    {
        _stream = stream;
        _ = ReadLoopAsync();
    }

    /// <summary>Is the driver installed at all (even if the AirPods aren't connected right now)?</summary>
    public static bool IsDriverInstalled() => InterfacePaths(presentOnly: false).Count > 0;

    /// <summary>Opens the first connected AirPods' channel, or null if none is available.</summary>
    public static AapDriverTransport? TryOpen()
    {
        foreach (var path in InterfacePaths(presentOnly: true))
        {
            var handle = CreateFile(path, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
            if (handle.IsInvalid) { Log.Write($"AAP driver: can't open interface (error {Marshal.GetLastWin32Error()})"); continue; }
            return new AapDriverTransport(new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true));
        }
        return null;
    }

    public async Task SendAsync(byte[] packet)
    {
        await _stream.WriteAsync(packet, _cts.Token);
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[1024];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer, _cts.Token);
                if (n <= 0) break;
                Received?.Invoke(buffer.AsSpan(0, n).ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Log.Write($"AAP channel closed: {ex.Message}");
        }
        Closed?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream.Dispose();
    }

    // ---- device interface lookup (cfgmgr32, no admin) ----

    private static List<string> InterfacePaths(bool presentOnly)
    {
        var guid = InterfaceGuid;
        uint flags = presentOnly ? CmGetDeviceInterfaceListPresent : CmGetDeviceInterfaceListAllDevices;
        if (CM_Get_Device_Interface_List_Size(out uint len, ref guid, null, flags) != 0 || len <= 1) return [];
        var buf = new char[len];
        if (CM_Get_Device_Interface_List(ref guid, null, buf, len, flags) != 0) return [];
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private const uint CmGetDeviceInterfaceListPresent = 0, CmGetDeviceInterfaceListAllDevices = 1;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, OpenExisting = 3, FileFlagOverlapped = 0x40000000;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_Size(out uint len, ref Guid cls, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List(ref Guid cls, string? deviceId, char[] buffer, uint len, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
