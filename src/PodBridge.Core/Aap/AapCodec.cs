namespace PodBridge.Core.Aap;

// Apple Accessory Protocol (AAP): the private protocol iPhone/Mac use to control AirPods, over a
// Bluetooth L2CAP channel (PSM 0x1001). Undocumented by Apple. The packet formats below come from
// public reverse-engineering notes (LibrePods documentation); no code was copied. They are
// UNVERIFIED on Windows until the PodBridge driver exists (see docs/advanced-features.md), and
// Apple can change them with any firmware update. Unknown packets are ignored, never guessed at.

/// <summary>Listening modes as the AirPods report them.</summary>
public enum NoiseMode : byte { Off = 0x01, NoiseCancellation = 0x02, Transparency = 0x03, Adaptive = 0x04 }

public enum PodPlacement : byte { InEar = 0x00, OutOfEar = 0x01, InCase = 0x02 }

public enum BatteryComponent : byte { Right = 0x02, Left = 0x04, Case = 0x08 }

public enum ChargeState : byte { Charging = 0x01, Discharging = 0x02, Disconnected = 0x04 }

public abstract record AapMessage;

/// <summary>Exact (1 %) battery per component.</summary>
public sealed record AapBattery(IReadOnlyList<AapBatteryEntry> Entries) : AapMessage
{
    public AapBatteryEntry? Get(BatteryComponent c) => Entries.FirstOrDefault(e => e.Component == c);
}

public sealed record AapBatteryEntry(BatteryComponent Component, int Percent, ChargeState State);

/// <summary>Where each pod is. Primary/secondary rather than left/right; the count is what matters.</summary>
public sealed record AapEarState(PodPlacement Primary, PodPlacement Secondary) : AapMessage
{
    public int InEarCount => (Primary == PodPlacement.InEar ? 1 : 0) + (Secondary == PodPlacement.InEar ? 1 : 0);
}

/// <summary>A settings value ("control command"): listening mode, Conversation Awareness, ...</summary>
public sealed record AapControl(byte Id, byte Value) : AapMessage
{
    public const byte ListeningMode = 0x0D;
    public const byte ConversationAwareness = 0x28;

    public NoiseMode? AsNoiseMode => Id == ListeningMode && Enum.IsDefined(typeof(NoiseMode), Value) ? (NoiseMode)Value : null;
    public bool? AsConversationAwareness => Id == ConversationAwareness ? Value == 0x01 : null;
}

/// <summary>Conversation Awareness: you started (higher level) or stopped speaking.</summary>
public sealed record AapSpeechLevel(byte Level) : AapMessage;

public static class AapCodec
{
    private const byte OpBattery = 0x04, OpEar = 0x06, OpControl = 0x09, OpSpeech = 0x4B;

    /// <summary>Opens the session. Must be the first packet.</summary>
    public static readonly byte[] Handshake = [0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Tells the AirPods which optional features the host supports (Conversation Awareness, Adaptive).</summary>
    public static readonly byte[] SetFeatureFlags = [0x04, 0x00, 0x04, 0x00, 0x4D, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Asks the AirPods to send battery, ear and settings updates.</summary>
    public static readonly byte[] RequestNotifications = [0x04, 0x00, 0x04, 0x00, 0x0F, 0x00, 0xFF, 0xFF, 0xFF, 0xFF];

    public static byte[] SetNoiseMode(NoiseMode mode) => Control(AapControl.ListeningMode, (byte)mode);

    public static byte[] SetConversationAwareness(bool on) => Control(AapControl.ConversationAwareness, on ? (byte)0x01 : (byte)0x02);

    private static byte[] Control(byte id, byte value) =>
        [0x04, 0x00, 0x04, 0x00, OpControl, 0x00, id, value, 0x00, 0x00, 0x00];

    /// <returns>The decoded message, or null for anything unknown or malformed.</returns>
    public static AapMessage? Parse(ReadOnlySpan<byte> p)
    {
        if (p.Length < 6 || p[0] != 0x04 || p[1] != 0x00 || p[2] != 0x04 || p[3] != 0x00 || p[5] != 0x00) return null;
        var body = p[6..];
        switch (p[4])
        {
            case OpBattery:
            {
                if (body.Length < 1) return null;
                int count = body[0];
                if (body.Length < 1 + count * 5) return null;
                var entries = new List<AapBatteryEntry>(count);
                for (int i = 0; i < count; i++)
                {
                    var e = body.Slice(1 + i * 5, 5);   // component, 0x01, level, state, 0x01
                    if (!Enum.IsDefined(typeof(BatteryComponent), e[0]) || e[2] > 100) continue;
                    entries.Add(new AapBatteryEntry((BatteryComponent)e[0], e[2], (ChargeState)e[3]));
                }
                return entries.Count > 0 ? new AapBattery(entries) : null;
            }
            case OpEar when body.Length >= 2 && body[0] <= 2 && body[1] <= 2:
                return new AapEarState((PodPlacement)body[0], (PodPlacement)body[1]);
            case OpControl when body.Length >= 2:
                return new AapControl(body[0], body[1]);
            case OpSpeech when body.Length >= 4 && body[0] == 0x02 && body[1] == 0x00 && body[2] == 0x01:
                return new AapSpeechLevel(body[3]);
            default:
                return null;
        }
    }
}
