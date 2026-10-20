namespace IntercomEventing.Samples.BlazorServer.Features.ServerRack;

public enum PowerState
{
    Off,
    On,
    Maintenance,
}

/// <summary>
/// The payload of a power change: who asked for it. The old and new state come from the call itself.
/// </summary>
public sealed record PowerChangeRequest(string RequestedBy);
