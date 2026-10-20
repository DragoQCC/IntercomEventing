namespace IntercomEventing;

/// <summary>
/// The call delivered by a <see cref="DebounceEvent{TPayload}"/> once a burst of triggers has settled (or was flushed).
/// </summary>
/// <typeparam name="TPayload">The payload type, or <see cref="NoPayload"/>.</typeparam>
/// <example>
/// <code>
/// searchBox.SearchChanged.Subscribe(call =>
///     Console.WriteLine($"searching '{call.Payload.Text}' after {call.CoalescedTriggerCount} keystrokes"));
/// </code>
/// </example>
public sealed class DebounceEventCall<TPayload> : EventCall<TPayload>
{
    /// <summary>
    /// The configured quiet period.
    /// </summary>
    public TimeSpan DebounceInterval { get; }

    /// <summary>
    /// How many triggers this raise absorbed (at least 1).
    /// </summary>
    public int CoalescedTriggerCount { get; }

    /// <summary>
    /// Creates the call. The library creates calls when the event raises; construct one yourself only to unit test a handler.
    /// </summary>
    /// <param name="debounceInterval">The configured quiet period.</param>
    /// <param name="coalescedTriggerCount">How many triggers this raise absorbed (at least 1).</param>
    /// <param name="payload">The payload of the last trigger.</param>
    /// <param name="metadata">Identifiers, timestamp and caller of the raise.</param>
    /// <param name="cancellationToken">Token exposed as <see cref="EventCall.CancellationToken"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// var call = new DebounceEventCall&lt;SearchQuery&gt;(TimeSpan.FromMilliseconds(300), coalescedTriggerCount: 4, new SearchQuery("ada"), metadata);
    /// </code>
    /// </example>
    public DebounceEventCall(TimeSpan debounceInterval, int coalescedTriggerCount, TPayload payload, EventMetadata metadata,
        CancellationToken cancellationToken = default)
        : base(payload, metadata, cancellationToken)
    {
        DebounceInterval = debounceInterval;
        CoalescedTriggerCount = coalescedTriggerCount;
    }
}
