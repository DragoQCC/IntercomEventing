using System.Numerics;

namespace IntercomEventing;

/// <summary>
/// The call delivered by a <see cref="ThresholdEvent{TValue,TPayload}"/>: the threshold, the values right before and after the
/// increment that raised, and that increment's payload.
/// </summary>
/// <typeparam name="TValue">The tracked numeric type.</typeparam>
/// <typeparam name="TPayload">The payload type, or <see cref="NoPayload"/>.</typeparam>
/// <example>
/// <code>
/// security.TooManyFailedLogins.Subscribe(call =>
///     Console.WriteLine($"{call.Payload.UserName}: {call.PreviousValue} -> {call.CurrentValue} (limit {call.Threshold})"));
/// </code>
/// </example>
public sealed class ThresholdEventCall<TValue, TPayload> : EventCall<TPayload>
    where TValue : INumber<TValue>
{
    /// <summary>
    /// The configured threshold.
    /// </summary>
    public TValue Threshold { get; }

    /// <summary>
    /// The value right before the increment that raised.
    /// </summary>
    public TValue PreviousValue { get; }

    /// <summary>
    /// The value right after the increment that raised; not affected by later increments.
    /// </summary>
    public TValue CurrentValue { get; }

    /// <summary>
    /// Creates the call. The library creates calls when the event raises; construct one yourself only to unit test a handler.
    /// </summary>
    /// <param name="threshold">The configured threshold.</param>
    /// <param name="previousValue">The value right before the increment that raised.</param>
    /// <param name="currentValue">The value right after the increment that raised.</param>
    /// <param name="payload">The data passed with that increment.</param>
    /// <param name="metadata">Identifiers, timestamp and caller of the raise.</param>
    /// <param name="cancellationToken">Token exposed as <see cref="EventCall.CancellationToken"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// var call = new ThresholdEventCall&lt;int, LoginAttempt&gt;(3, 2, 3, new LoginAttempt("ada", "10.0.0.1"), metadata);
    /// </code>
    /// </example>
    public ThresholdEventCall(TValue threshold, TValue previousValue, TValue currentValue, TPayload payload, EventMetadata metadata,
        CancellationToken cancellationToken = default)
        : base(payload, metadata, cancellationToken)
    {
        Threshold = threshold;
        PreviousValue = previousValue;
        CurrentValue = currentValue;
    }
}
