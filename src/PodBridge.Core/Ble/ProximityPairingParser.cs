namespace PodBridge.Core.Ble;

public sealed record AirPodsStatus(
    ushort ModelId,
    int? LeftPercent,
    int? RightPercent,
    int? CasePercent,
    bool LeftCharging,
    bool RightCharging,
    bool CaseCharging,
    bool LeftInEar,
    bool RightInEar,
    short Rssi,
    DateTimeOffset Timestamp)
{
    /// <summary>Lowest known pod battery, used for the tray icon.</summary>
    public int? LowestPodPercent =>
        (LeftPercent, RightPercent) switch
        {
            (int l, int r) => Math.Min(l, r),
            (int l, null) => l,
            (null, int r) => r,
            _ => null,
        };
}

/// <summary>
/// Parses Apple manufacturer data (company ID 0x004C, payload starting at the TLV type byte; Windows'
/// BluetoothLEManufacturerData.Data already strips the company ID).
///
/// Proximity pairing payload (after type 0x07 and length):
///   [0] prefix   [1..2] model id   [3] status (in-ear bits, orientation flag)
///   [4] pod battery nibbles   [5] high nibble: charging flags, low nibble: case battery
///   [6] lid counter   ... rest is encrypted / not needed.
/// Battery nibble 0..10 = 0..100 %, 15 = unknown (pod not in range / in closed case).
/// Battery comes in 10 % steps; that is all Apple broadcasts in the clear.
/// </summary>
public static class ProximityPairingParser
{
    public const ushort AppleCompanyId = 0x004C;
    private const byte ProximityPairingType = 0x07;
    private const int MinPayload = 7;

    public static AirPodsStatus? TryParse(ReadOnlySpan<byte> appleData, short rssi, DateTimeOffset timestamp)
    {
        int i = 0;
        while (i + 2 <= appleData.Length)
        {
            byte type = appleData[i];
            int len = appleData[i + 1];
            int start = i + 2;
            if (start + len > appleData.Length) return null; // truncated / malformed
            if (type == ProximityPairingType && len >= MinPayload)
                return ParsePayload(appleData.Slice(start, len), rssi, timestamp);
            i = start + len;
        }
        return null;
    }

    private static AirPodsStatus ParsePayload(ReadOnlySpan<byte> p, short rssi, DateTimeOffset ts)
    {
        ushort model = (ushort)((p[1] << 8) | p[2]);
        byte status = p[3];
        byte pods = p[4];
        byte flagsCase = p[5];

        // When bit 0x20 of status is clear, the left/right nibbles are swapped (it tracks which pod is
        // the "primary"). Mapping matches OpenPods' long-standing implementation.
        bool flip = (status & 0x20) == 0;

        int leftNib = flip ? pods >> 4 : pods & 0x0F;
        int rightNib = flip ? pods & 0x0F : pods >> 4;
        int caseNib = flagsCase & 0x0F;
        int charge = flagsCase >> 4;
        int ear = status & 0x0F;

        return new AirPodsStatus(
            ModelId: model,
            LeftPercent: Battery(leftNib),
            RightPercent: Battery(rightNib),
            CasePercent: Battery(caseNib),
            LeftCharging: (charge & (flip ? 0b010 : 0b001)) != 0,
            RightCharging: (charge & (flip ? 0b001 : 0b010)) != 0,
            CaseCharging: (charge & 0b100) != 0,
            LeftInEar: (ear & (flip ? 0x08 : 0x02)) != 0,
            RightInEar: (ear & (flip ? 0x02 : 0x08)) != 0,
            Rssi: rssi,
            Timestamp: ts);
    }

    private static int? Battery(int nibble) => nibble switch
    {
        <= 10 => nibble * 10,
        _ => null,
    };
}
