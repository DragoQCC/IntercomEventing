namespace IntercomEventing.Samples.BlazorServer.Shared;

/// <summary>
/// A short name for the browser tab using the app. Registered as scoped, so each circuit (tab) gets its own name, which the
/// pages pass as the caller or author of what this tab raises.
/// </summary>
public sealed class CircuitVisitor
{
    public string Name { get; } = $"Tab {Random.Shared.Next(0x1000, 0xFFFF):X4}";
}
