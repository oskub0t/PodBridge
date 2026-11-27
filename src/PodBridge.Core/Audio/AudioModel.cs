namespace PodBridge.Core.Audio;

public enum Flow { Render, Capture }

/// <summary>Windows' three default-device roles. "Console" and "Multimedia" are what the Sound
/// settings call the default device; "Communications" is the default communications device.</summary>
public enum Role { Console = 0, Multimedia = 1, Communications = 2 }

public sealed record AudioEndpoint(string Id, string Name, Flow Flow, bool Active);

public interface IAudioBackend
{
    IReadOnlyList<AudioEndpoint> GetEndpoints();
    string? GetDefaultId(Flow flow, Role role);
    void SetDefault(string endpointId, Role role);
}

public interface IMicLevelSource
{
    /// <summary>Peak level 0..1 on the endpoint right now, or null if it can't be read.</summary>
    float? GetPeak(string endpointId);
}
