namespace IntercomEventing.Benchmark.Examples.StateChange;

public enum PowerState
{
    Off,
    Starting,
    On,
}

/// <summary>
/// The payload delivered with each power change: who asked for it.
/// </summary>
public sealed record PowerChangeRequest(string RequestedBy);

/// <summary>
/// A server whose power state is a <see cref="StateChangeEvent{TState,TPayload}"/>: setting the same state twice raises once.
/// </summary>
public sealed class Server : IAsyncDisposable
{
    public string Name { get; }

    public StateChangeEvent<PowerState, PowerChangeRequest> PowerChanged { get; } = new(PowerState.Off);

    public Server(string name)
    {
        Name = name;
    }

    /// <returns><see langword="true"/> when the state changed and the event was raised.</returns>
    public Task<bool> SetPowerAsync(PowerState newState, string requestedBy)
    {
        return PowerChanged.ChangeStateAsync(newState, new PowerChangeRequest(requestedBy), caller: this);
    }

    public ValueTask DisposeAsync()
    {
        return PowerChanged.DisposeAsync();
    }
}
