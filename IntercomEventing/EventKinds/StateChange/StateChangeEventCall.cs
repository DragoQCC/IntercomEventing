namespace IntercomEventing;

/// <summary>
/// The call delivered by a <see cref="StateChangeEvent{TState,TPayload}"/>: the state before and after the change, and the
/// payload passed with it.
/// </summary>
/// <typeparam name="TState">The state type.</typeparam>
/// <typeparam name="TPayload">The payload type, or <see cref="NoPayload"/>.</typeparam>
/// <example>
/// <code>
/// datacenter.PowerChanged.Subscribe(call =>
///     Console.WriteLine($"{call.Payload.Name}: {call.OldState} -> {call.NewState}"));
/// </code>
/// </example>
public sealed class StateChangeEventCall<TState, TPayload> : EventCall<TPayload>
{
    /// <summary>
    /// The state before the change.
    /// </summary>
    public TState OldState { get; }

    /// <summary>
    /// The state after the change; not affected by later changes.
    /// </summary>
    public TState NewState { get; }

    /// <summary>
    /// Creates the call. The library creates calls when the event raises; construct one yourself only to unit test a handler.
    /// </summary>
    /// <param name="oldState">The state before the change.</param>
    /// <param name="newState">The state after the change.</param>
    /// <param name="payload">The data passed with the change.</param>
    /// <param name="metadata">Identifiers, timestamp and caller of the raise.</param>
    /// <param name="cancellationToken">Token exposed as <see cref="EventCall.CancellationToken"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// var call = new StateChangeEventCall&lt;PowerState, Server&gt;(PowerState.Off, PowerState.On, webServer, metadata);
    /// </code>
    /// </example>
    public StateChangeEventCall(TState oldState, TState newState, TPayload payload, EventMetadata metadata,
        CancellationToken cancellationToken = default)
        : base(payload, metadata, cancellationToken)
    {
        OldState = oldState;
        NewState = newState;
    }
}
