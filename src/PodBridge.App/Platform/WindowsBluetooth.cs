using Windows.Devices.Enumeration;

namespace PodBridge.App.Platform;

/// <summary>
/// Reads what Windows itself knows about the paired AirPods. Windows receives a battery level over
/// the hands-free (HFP) link while they're connected, and that's the number its Bluetooth settings
/// show. It is one overall value, not left/right/case, but it doesn't depend on BLE advertisements
/// at all. Readable as a normal user (the same data as PowerShell's Get-PnpDeviceProperty).
/// </summary>
internal static class WindowsBluetooth
{
    // DEVPKEY_Bluetooth_Battery
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public static async Task<int?> GetBatteryAsync(string deviceName)
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                "System.ItemNameDisplay:~~\"AirPods\"", [BatteryKey], DeviceInformationKind.Device);

            int? best = null;
            foreach (var d in devices)
            {
                if (!d.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                if (d.Properties.TryGetValue(BatteryKey, out var v) && v is byte b && b <= 100)
                    best = best is int x ? Math.Min(x, b) : b;
            }
            return best;
        }
        catch (Exception ex)
        {
            Log.Write($"Windows battery read failed: {ex.Message}");
            return null;
        }
    }
}
