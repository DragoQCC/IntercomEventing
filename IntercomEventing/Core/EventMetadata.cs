namespace IntercomEventing;

/// <summary>
/// Describes one raise of an event. Created by the library for every raise and never changed afterwards.
/// </summary>
/// <param name="EventId">Identifies the event instance; the same for every raise of that event.</param>
/// <param name="EventName">Display name of the event (<see cref="IntercomEventBase{TCall}.Name"/>), useful in logs.</param>
/// <param name="EventCallId">Identifies this raise: a version 7 GUID stamped with <paramref name="RaisedAt"/>, so the ids of one
/// clock sort by raise time. A version 7 GUID cannot hold a time before 1970-01-01; when the event's
/// <see cref="EventingOptions.TimeProvider"/> reports one, the id is stamped with the current system time instead, and
/// <paramref name="RaisedAt"/> still keeps the provider's value.</param>
/// <param name="RaisedAt">When the raise started dispatching, taken from <see cref="EventingOptions.TimeProvider"/>.</param>
/// <param name="Caller">The optional object passed as <c>caller</c> when the event was raised.</param>
/// <example>
/// <code>
/// registry.UserCreated.Subscribe(call =>
///     logger.LogInformation("{Event} call {CallId} at {RaisedAt}", call.Metadata.EventName, call.Metadata.EventCallId, call.Metadata.RaisedAt));
/// </code>
/// </example>
public sealed record EventMetadata(Guid EventId, string EventName, Guid EventCallId, DateTimeOffset RaisedAt, object? Caller);
