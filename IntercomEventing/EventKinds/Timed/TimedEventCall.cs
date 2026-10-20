namespace IntercomEventing;

/// <summary>
/// The call delivered by a <see cref="TimedEvent{TPayload}"/> on each tick.
/// </summary>
/// <typeparam name="TPayload">The payload type, or <see cref="NoPayload"/>.</typeparam>
/// <example>
/// <code>
/// sessionExpired.Subscribe(call => Console.WriteLine($"{call.Payload.Name} timed out after {call.Interval} (tick {call.TickNumber})"));
/// </code>
/// </example>
public sealed class TimedEventCall<TPayload> : EventCall<TPayload>
{
    /// <summary>
    /// The timer interval.
    /// </summary>
    public TimeSpan Interval { get; }

    /// <summary>
    /// 1 for the first tick after <c>Start</c>, then 2, 3, ...; a new <c>Start</c> counts from 1 again.
    /// </summary>
    public long TickNumber { get; }

    /// <summary>
    /// Creates the call. The library creates calls when the timer ticks; construct one yourself only to unit test a handler.
    /// </summary>
    /// <param name="interval">The timer interval.</param>
    /// <param name="tickNumber">1 for the first tick after <c>Start</c>, then 2, 3, ...</param>
    /// <param name="payload">The data passed to <c>Start</c>.</param>
    /// <param name="metadata">Identifiers, timestamp and caller of the raise.</param>
    /// <param name="cancellationToken">Token exposed as <see cref="EventCall.CancellationToken"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// var call = new TimedEventCall&lt;SessionUser&gt;(TimeSpan.FromMinutes(20), tickNumber: 1, new SessionUser("Ada"), metadata);
    /// </code>
    /// </example>
    public TimedEventCall(TimeSpan interval, long tickNumber, TPayload payload, EventMetadata metadata, CancellationToken cancellationToken = default)
        : base(payload, metadata, cancellationToken)
    {
        Interval = interval;
        TickNumber = tickNumber;
    }
}
