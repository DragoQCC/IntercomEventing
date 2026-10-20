namespace IntercomEventing.Samples.BlazorServer.Features.ServerRack;

/// <summary>
/// One server in the rack. Its power state lives in a <see cref="StateChangeEvent{TState, TPayload}"/>, which raises only when
/// the state really changes and gives every handler the old and new state.
/// </summary>
public sealed class RackServer : IAsyncDisposable
{
    public string Name { get; }

    public StateChangeEvent<PowerState, PowerChangeRequest> PowerChanged { get; }

    public RackServer(string name)
    {
        Name = name;
        PowerChanged = new StateChangeEvent<PowerState, PowerChangeRequest>(PowerState.Off) { Name = $"{name} power" };
    }

    /// <summary>
    /// Switches the server and waits until every handler (in every open tab) has run.
    /// </summary>
    /// <returns><see langword="false"/> when the server was already in <paramref name="newState"/>, so nothing was raised.</returns>
    public Task<bool> SetPowerAsync(PowerState newState, string requestedBy)
    {
        return PowerChanged.ChangeStateAsync(newState, new PowerChangeRequest(requestedBy), caller: this);
    }

    //the server created the event, so it disposes it
    public ValueTask DisposeAsync()
    {
        return PowerChanged.DisposeAsync();
    }
}
