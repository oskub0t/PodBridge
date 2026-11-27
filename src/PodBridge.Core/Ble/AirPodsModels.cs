namespace PodBridge.Core.Ble;

/// <summary>
/// Model IDs carried in bytes 3-4 of Apple's "proximity pairing" advertisement (type 0x07).
/// Values come from community reverse-engineering (OpenPods, LibrePods, furiousMAC). Newer models are
/// less certain, so the app also has a "learn my AirPods" step that stores whatever ID your own pair
/// broadcasts. Unknown IDs are shown as "Apple audio device 0xXXXX" rather than dropped.
/// </summary>
public static class AirPodsModels
{
    private static readonly Dictionary<ushort, string> Known = new()
    {
        [0x0220] = "AirPods (1st gen)",
        [0x0F20] = "AirPods (2nd gen)",
        [0x1320] = "AirPods (3rd gen)",
        [0x1920] = "AirPods 4",              // less certain
        [0x1B20] = "AirPods 4 (ANC)",        // less certain
        [0x0E20] = "AirPods Pro",
        [0x1420] = "AirPods Pro 2",
        [0x2420] = "AirPods Pro 2 (USB-C)",
        [0x0A20] = "AirPods Max",
        [0x1F20] = "AirPods Max (USB-C)",    // less certain
        // AirPods Pro 3: ID not confirmed. Use "Learn my AirPods" and it will be stored in config.
    };

    public static string NameFor(ushort modelId, IReadOnlyDictionary<ushort, string>? extra = null)
    {
        if (extra is not null && extra.TryGetValue(modelId, out var custom)) return custom;
        return Known.TryGetValue(modelId, out var name) ? name : $"Apple audio device 0x{modelId:X4}";
    }

    public static bool IsKnown(ushort modelId) => Known.ContainsKey(modelId);

    /// <summary>
    /// Every genuine Apple audio model ID seen so far ends in 0x20 (0x0E20, 0x1420, 0x2720...).
    /// Used to reject IDs saved from misread (encrypted) adverts by earlier versions.
    /// </summary>
    public static bool LooksValid(ushort modelId) => (modelId & 0xFF) == 0x20;
}
