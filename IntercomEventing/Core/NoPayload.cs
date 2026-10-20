namespace IntercomEventing;

/// <summary>
/// The payload type of events that carry no data of their own, such as a plain heartbeat timer or a counter threshold.
/// </summary>
/// <remarks>
/// You rarely name this type: the payload-less convenience classes (<see cref="IntercomEvent"/>, <see cref="ThresholdEvent{TValue}"/>,
/// <see cref="StateChangeEvent{TState}"/>, <see cref="TimedEvent"/>, <see cref="DebounceEvent"/>) use it for you.
/// It does appear in their call types, for example <c>ThresholdEventCall&lt;int, NoPayload&gt;</c>.
/// </remarks>
/// <example>
/// <code>
/// var heartbeat = new TimedEvent(TimeSpan.FromSeconds(30));
/// heartbeat.Subscribe((TimedEventCall&lt;NoPayload&gt; call) => Console.WriteLine($"tick {call.TickNumber}"));
/// </code>
/// </example>
public readonly record struct NoPayload
{
    /// <summary>
    /// The single value of this type.
    /// </summary>
    public static NoPayload Value => default;

    /// <inheritdoc />
    public override string ToString()
    {
        return "(no payload)";
    }
}
