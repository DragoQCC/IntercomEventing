namespace IntercomEventing.Samples.WebApi.Features.Store;

public enum StoreStatus
{
    Open,
    Paused,
    Closed,
}

/// <summary>
/// The payload of a status change. The old and new status come from the call itself.
/// </summary>
public sealed record StoreStatusChange(string Reason);

/// <summary>
/// Whether the store accepts orders. The status lives in a <see cref="StateChangeEvent{TState, TPayload}"/>, which raises
/// only when it really changes.
/// </summary>
public sealed class StoreService : IAsyncDisposable
{
    private readonly StateChangeEvent<StoreStatus, StoreStatusChange> _statusChanged = new(StoreStatus.Open) { Name = "StoreStatusChanged" };

    //the subscribe-only view: subscribers cannot change the status by raising the event themselves
    public IIntercomEvent<StateChangeEventCall<StoreStatus, StoreStatusChange>> StatusChanged => _statusChanged;

    public StoreStatus CurrentStatus => _statusChanged.CurrentState;

    /// <returns><see langword="false"/> when the store already had <paramref name="newStatus"/>, so nothing was raised.</returns>
    public Task<bool> ChangeStatusAsync(StoreStatus newStatus, string reason)
    {
        return _statusChanged.ChangeStateAsync(newStatus, new StoreStatusChange(reason), caller: this);
    }

    public ValueTask DisposeAsync()
    {
        return _statusChanged.DisposeAsync();
    }
}
