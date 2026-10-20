namespace IntercomEventing;

/// <summary>
/// Builds the call for one raise. <see cref="IntercomEventBase{TCall}"/> supplies the metadata and the cancellation token;
/// the event kind supplies the raise-specific <paramref name="state"/> (payload, values, counters).
/// </summary>
/// <remarks>
/// The state is passed separately so an event kind can use a <see langword="static"/> lambda: no closure is allocated, and
/// a raise with no subscribers never calls the factory, so it allocates nothing.
/// </remarks>
/// <typeparam name="TState">The raise-specific data, typically the payload or a value tuple.</typeparam>
/// <typeparam name="TCall">The call type of the event being raised.</typeparam>
/// <param name="state">The raise-specific data passed to <see cref="IntercomEventBase{TCall}.DispatchAsync{TState}"/>.</param>
/// <param name="metadata">Identifiers, timestamp and caller of the raise.</param>
/// <param name="cancellationToken">The token to expose as <see cref="EventCall.CancellationToken"/>.</param>
/// <returns>The call every handler of this raise receives.</returns>
/// <example>
/// <code>
/// return DispatchAsync(newCount, static (count, metadata, token) => new EventCall&lt;int&gt;(count, metadata, token), caller: this, cancellationToken);
/// </code>
/// </example>
public delegate TCall EventCallFactory<in TState, out TCall>(TState state, EventMetadata metadata, CancellationToken cancellationToken)
    where TCall : EventCall;
