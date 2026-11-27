using PodBridge.Core.Ble;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace PodBridge.App.Platform;

/// <summary>Listens for Apple BLE advertisements. No pairing, no admin, unpackaged-app friendly.
/// Events fire on a thread-pool thread.</summary>
internal sealed class BleScanner : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new()
    {
        ScanningMode = BluetoothLEScanningMode.Passive,
    };

    public event Action<AirPodsStatus>? Advertisement;

    /// <summary>Raised when scanning stops unexpectedly, with a human explanation.</summary>
    public event Action<string>? Failed;

    public BleScanner()
    {
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) =>
        {
            if (e.Error == BluetoothError.Success) return;
            Failed?.Invoke(e.Error switch
            {
                BluetoothError.RadioNotAvailable => "Bluetooth is off or missing.",
                BluetoothError.DisabledByPolicy => "Bluetooth scanning is disabled by your organisation's policy.",
                BluetoothError.DisabledByUser => "Bluetooth access is turned off in Windows privacy settings.",
                _ => $"Bluetooth scanning stopped ({e.Error}).",
            });
        };
    }

    public void Start() => _watcher.Start();
    public void Stop() => _watcher.Stop();
    public bool IsRunning => _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        foreach (var md in e.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != ProximityPairingParser.AppleCompanyId) continue;
            var bytes = new byte[md.Data.Length];
            using (var reader = DataReader.FromBuffer(md.Data)) reader.ReadBytes(bytes);
            var status = ProximityPairingParser.TryParse(bytes, e.RawSignalStrengthInDBm, e.Timestamp);
            if (status is not null) Advertisement?.Invoke(status);
        }
    }

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        if (IsRunning) _watcher.Stop();
    }
}
