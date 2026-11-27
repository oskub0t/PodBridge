using System.Runtime.InteropServices;
using PodBridge.App.Interop;
using PodBridge.Core.Audio;

namespace PodBridge.App.Platform;

/// <summary>Core Audio wrapper: list endpoints, read/set defaults, read mic level, (re)connect BT audio.
/// Call from the UI (STA) thread.</summary>
internal sealed class WindowsAudio : IAudioBackend, IMicLevelSource
{
    private static readonly Guid IidDeviceTopology = typeof(IDeviceTopology).GUID;
    private static readonly Guid IidKsControl = typeof(IKsControl).GUID;
    private static readonly Guid IidMeter = typeof(IAudioMeterInformation).GUID;

    private readonly IMMDeviceEnumerator _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

    public IReadOnlyList<AudioEndpoint> GetEndpoints() =>
        Enumerate(EDataFlow.eRender, Flow.Render).Concat(Enumerate(EDataFlow.eCapture, Flow.Capture)).ToList();

    private IEnumerable<AudioEndpoint> Enumerate(EDataFlow df, Flow flow)
    {
        // Unplugged = paired but not connected; we need those to reconnect.
        if (_enumerator.EnumAudioEndpoints(df, DeviceState.Active | DeviceState.Unplugged, out var col) != 0) yield break;
        col.GetCount(out var n);
        for (uint i = 0; i < n; i++)
        {
            if (col.Item(i, out var dev) != 0) continue;
            dev.GetId(out var id);
            dev.GetState(out var state);
            yield return new AudioEndpoint(id, FriendlyName(dev) ?? id, flow, state == DeviceState.Active);
        }
    }

    private static string? FriendlyName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0 /*STGM_READ*/, out var store) != 0) return null;
        var key = PropertyKey.DeviceFriendlyName;
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.AsString(); }
        finally { PropVariant.PropVariantClear(ref pv); }
    }

    public string? GetDefaultId(Flow flow, Role role)
    {
        var df = flow == Flow.Render ? EDataFlow.eRender : EDataFlow.eCapture;
        if (_enumerator.GetDefaultAudioEndpoint(df, (ERole)role, out var dev) != 0) return null;
        dev.GetId(out var id);
        return id;
    }

    public void SetDefault(string endpointId, Role role)
    {
        var policy = (IPolicyConfig)new CPolicyConfigClient();
        int hr = policy.SetDefaultEndpoint(endpointId, (ERole)role);
        if (hr != 0) Log.Write($"SetDefaultEndpoint({role}) failed 0x{hr:X8}");
    }

    public float? GetPeak(string endpointId)
    {
        try
        {
            if (_enumerator.GetDevice(endpointId, out var dev) != 0) return null;
            var iid = IidMeter;
            if (dev.Activate(ref iid, Clsctx.All, IntPtr.Zero, out var o) != 0) return null;
            ((IAudioMeterInformation)o).GetPeakValue(out var peak);
            return peak;
        }
        catch (COMException) { return null; }
    }

    /// <summary>
    /// Asks the Bluetooth audio driver to connect/disconnect the device that owns these endpoints,
    /// exactly like Settings → Bluetooth → Connect. Works without admin; the device must be paired.
    /// Returns how many driver filters accepted the request.
    /// </summary>
    public int RequestConnection(IEnumerable<AudioEndpoint> endpointsOfDevice, bool connect)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int ok = 0;
        foreach (var ep in endpointsOfDevice)
        {
            try
            {
                if (_enumerator.GetDevice(ep.Id, out var dev) != 0) continue;
                var iidTopo = IidDeviceTopology;
                if (dev.Activate(ref iidTopo, Clsctx.All, IntPtr.Zero, out var t) != 0) continue;
                var topo = (IDeviceTopology)t;
                if (topo.GetConnector(0, out var conn) != 0) continue;
                if (conn.GetDeviceIdConnectedTo(out var filterId) != 0 || !done.Add(filterId)) continue;
                if (_enumerator.GetDevice(filterId, out var filter) != 0) continue;
                var iidKs = IidKsControl;
                if (filter.Activate(ref iidKs, Clsctx.All, IntPtr.Zero, out var k) != 0) continue;

                var prop = new KsProperty
                {
                    Set = KsBtAudio.PropSet,
                    Id = connect ? KsBtAudio.OneShotReconnect : KsBtAudio.OneShotDisconnect,
                    Flags = KsBtAudio.KsPropertyTypeGet,
                };
                int hr = ((IKsControl)k).KsProperty(ref prop, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);
                Log.Write($"BT {(connect ? "connect" : "disconnect")} via {ep.Name}: 0x{hr:X8}");
                if (hr == 0) ok++;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                Log.Write($"BT request failed on {ep.Name}: {ex.Message}");
            }
        }
        return ok;
    }
}
