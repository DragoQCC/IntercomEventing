namespace IntercomEventing.Samples.BlazorServer.EventKinds.Throttle;

/// <summary>
/// The call delivered by a <see cref="ThrottleEvent{TPayload}"/>. It derives from <see cref="EventCall{TPayload}"/>, so a
/// handler written for <c>EventCall&lt;TPayload&gt;</c> (or for the <see cref="EventCall"/> base) also attaches to a throttle
/// event.
/// </summary>
/// <typeparam name="TPayload">The payload of the offer that was raised.</typeparam>
public sealed class ThrottleEventCall<TPayload> : EventCall<TPayload>
{
    /// <summary>
    /// The minimum time between two raises of the event.
    /// </summary>
    public TimeSpan Interval { get; }

    /// <summary>
    /// How many offers were replaced by a later offer, and so never raised, since the previous raise.
    /// </summary>
    public int DroppedOfferCount { get; }

    public ThrottleEventCall(TimeSpan interval, int droppedOfferCount, TPayload payload, EventMetadata metadata,
        CancellationToken cancellationToken = default)
        : base(payload, metadata, cancellationToken)
    {
        Interval = interval;
        DroppedOfferCount = droppedOfferCount;
    }
}
