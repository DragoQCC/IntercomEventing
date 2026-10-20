namespace IntercomEventing;

/// <summary>
/// Holds a current state and raises only when it actually changes. Each raise carries the old state, the new state and
/// the payload passed to that change (for example, which server changed power state).
/// </summary>
/// <remarks>
/// <para>The compare-and-set is atomic, so every call has a consistent old/new pair and no change is lost.
/// <see langword="null"/> states are supported.</para>
/// <para>Raises are not serialized: changes from several threads deliver concurrently, so handlers can see the calls of
/// concurrent changes interleaved or out of order. Await each change if subscribers need strict ordering.</para>
/// </remarks>
/// <typeparam name="TState">The state type; compared with the supplied comparer or <see cref="EqualityComparer{T}.Default"/>.</typeparam>
/// <typeparam name="TPayload">Data passed with each change.</typeparam>
/// <example>
/// <code>
/// public StateChangeEvent&lt;PowerState, Server&gt; PowerChanged { get; } = new(PowerState.Off);
///
/// await PowerChanged.ChangeStateAsync(PowerState.On, server);
///
/// datacenter.PowerChanged.Subscribe(call =>
///     Console.WriteLine($"{call.Payload.Name}: {call.OldState} -> {call.NewState}"));
/// </code>
/// </example>
public class StateChangeEvent<TState, TPayload> : IntercomEventBase<StateChangeEventCall<TState, TPayload>>
{
    private readonly Lock _stateLock = new();
    private readonly IEqualityComparer<TState> _stateComparer;
    private TState _currentState;

    /// <summary>
    /// The current state. Change it with <see cref="ChangeStateAsync(TState, TPayload, object?, CancellationToken)"/>, which can be awaited.
    /// </summary>
    public TState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
    }

    /// <summary>
    /// Creates the event in <paramref name="initialState"/>.
    /// </summary>
    /// <param name="initialState">The state before any change.</param>
    /// <param name="stateComparer">Decides whether a new state differs from the current one; defaults to
    /// <see cref="EqualityComparer{T}.Default"/>. It runs inside the update lock, so keep it fast.</param>
    /// <param name="options">Dispatch settings; <see langword="null"/> uses <see cref="EventingOptions.Default"/> at each raise.</param>
    /// <example>
    /// <code>
    /// var region = new StateChangeEvent&lt;string, User&gt;("eu-west", StringComparer.OrdinalIgnoreCase);
    /// </code>
    /// </example>
    public StateChangeEvent(TState initialState, IEqualityComparer<TState>? stateComparer = null, EventingOptions? options = null)
        : base(options)
    {
        _currentState = initialState;
        _stateComparer = stateComparer ?? EqualityComparer<TState>.Default;
    }

    /// <summary>
    /// Sets the state and raises if it differs from the current one.
    /// </summary>
    /// <remarks>
    /// A cancelled <paramref name="cancellationToken"/> or a disposed event is reported before the state changes; cancellation
    /// while handlers are starting leaves the new state applied.
    /// </remarks>
    /// <param name="newState">The state to move to.</param>
    /// <param name="payload">Data delivered with the raise, if the state changed.</param>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/>.</param>
    /// <param name="cancellationToken">Stops starting further handlers.</param>
    /// <returns><see langword="true"/> if the state changed and the event was raised; <see langword="false"/> if the new state
    /// equals the current one.</returns>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example>
    /// <code>
    /// bool changed = await monitor.PowerChanged.ChangeStateAsync(PowerState.On, webServer, caller: this);
    /// </code>
    /// </example>
    public async Task<bool> ChangeStateAsync(TState newState, TPayload payload, object? caller = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        TState oldState;
        lock (_stateLock)
        {
            var stateIsUnchanged = _stateComparer.Equals(_currentState, newState);
            if (stateIsUnchanged)
            {
                return false;
            }
            oldState = _currentState;
            _currentState = newState;
        }

        //the old/new pair is captured per change and travels in the call, so concurrent raises never share it
        var raiseState = (OldState: oldState, NewState: newState, Payload: payload);
        await DispatchAsync(
            raiseState,
            static (state, metadata, callCancellationToken) => new StateChangeEventCall<TState, TPayload>(
                state.OldState,
                state.NewState,
                state.Payload,
                metadata,
                callCancellationToken),
            caller,
            cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>
/// A <see cref="StateChangeEvent{TState,TPayload}"/> without a payload.
/// </summary>
/// <typeparam name="TState">The state type.</typeparam>
/// <example>
/// <code>
/// var connection = new StateChangeEvent&lt;ConnectionState&gt;(ConnectionState.Disconnected);
/// connection.Subscribe(call => Console.WriteLine($"{call.OldState} -> {call.NewState}"));
/// await connection.ChangeStateAsync(ConnectionState.Connected);
/// </code>
/// </example>
public class StateChangeEvent<TState> : StateChangeEvent<TState, NoPayload>
{
    /// <summary>
    /// Creates the event in <paramref name="initialState"/>.
    /// </summary>
    /// <param name="initialState">The state before any change.</param>
    /// <param name="stateComparer">Decides whether a new state differs from the current one; defaults to <see cref="EqualityComparer{T}.Default"/>.</param>
    /// <param name="options">Dispatch settings; <see langword="null"/> uses <see cref="EventingOptions.Default"/> at each raise.</param>
    /// <example>
    /// <code>
    /// var connection = new StateChangeEvent&lt;ConnectionState&gt;(ConnectionState.Disconnected);
    /// </code>
    /// </example>
    public StateChangeEvent(TState initialState, IEqualityComparer<TState>? stateComparer = null, EventingOptions? options = null)
        : base(initialState, stateComparer, options)
    {
    }

    /// <summary>
    /// Sets the state and raises if it differs from the current one.
    /// </summary>
    /// <param name="newState">The state to move to.</param>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/>.</param>
    /// <param name="cancellationToken">Stops starting further handlers.</param>
    /// <returns><see langword="true"/> if the state changed and the event was raised.</returns>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example>
    /// <code>
    /// bool changed = await connection.ChangeStateAsync(ConnectionState.Connected);
    /// </code>
    /// </example>
    public Task<bool> ChangeStateAsync(TState newState, object? caller = null, CancellationToken cancellationToken = default)
    {
        return ChangeStateAsync(newState, NoPayload.Value, caller, cancellationToken);
    }
}
