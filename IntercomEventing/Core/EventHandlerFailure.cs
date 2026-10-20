namespace IntercomEventing;

/// <summary>
/// Which part of an event failed, as reported in <see cref="EventHandlerFailure.Stage"/>.
/// </summary>
public enum EventFailureStage
{
    /// <summary>
    /// The event handler threw or returned a faulted task.
    /// </summary>
    Handler,

    /// <summary>
    /// An <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> hook failed after its <c>SubscribeAsync</c> call had stopped
    /// waiting for it because the caller's token was cancelled. Nothing was registered.
    /// </summary>
    OnSubscribed,

    /// <summary>
    /// An <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> hook failed and no caller could receive its exception: a
    /// hook started in the background by <c>Raised -=</c>, <c>Unsubscribe(handler)</c> or a synchronous <c>Dispose()</c>, a
    /// hook run (and awaited) by event disposal, or a hook run to undo an <see cref="SubscriptionOptions{TCall}.OnSubscribed"/>
    /// hook whose subscription was never registered. A hook run by <see cref="EventSubscription{TCall}.UnsubscribeAsync"/> or
    /// <see cref="EventSubscription{TCall}.DisposeAsync"/> throws to that caller instead.
    /// </summary>
    OnUnsubscribed,

    /// <summary>
    /// The background work of a <see cref="TimedEvent{TPayload}"/> or <see cref="DebounceEvent{TPayload}"/> (or of a custom
    /// event kind that reports through <see cref="IntercomEventBase{TCall}.RouteBackgroundRaiseFailure"/>) failed outside any
    /// handler, for example because a custom <see cref="TimeProvider"/> threw while the next tick or quiet period was being
    /// scheduled. A timed event's schedule stops; a debounce event drops that raise and accepts new triggers.
    /// </summary>
    BackgroundRaise,

    /// <summary>
    /// A step of <see cref="IntercomEventBase{TCall}.DisposeAsync"/> failed: a callback that a handler registered on
    /// <see cref="EventCall.CancellationToken"/> threw when disposal cancelled it, or the event kind's own shutdown threw.
    /// Disposal still completes.
    /// </summary>
    Disposal,

    /// <summary>
    /// The dispatcher failed outside any handler while timing one: a custom <see cref="TimeProvider"/> threw while the timeout
    /// of <see cref="EventingOptions.StartNextEventHandlerAfter"/> was being started or stopped. The raise still completes and
    /// the handler is still observed, so its own exceptions are routed as <see cref="Handler"/>. A handler whose timeout could
    /// not be started is waited for without one, as if <see cref="EventingOptions.StartNextEventHandlerAfter"/> were
    /// <see langword="null"/>.
    /// </summary>
    Dispatch,
}

/// <summary>
/// Describes an exception that reached <see cref="EventingOptions.DefaultExceptionHandler"/>.
/// </summary>
/// <param name="Exception">The exception that was thrown.</param>
/// <param name="Stage">Whether a handler, a subscription hook, the dispatcher, the event's own background work or its disposal failed.</param>
/// <param name="EventName">The <see cref="IntercomEventBase{TCall}.Name"/> of the event.</param>
/// <param name="SubscriptionId">The <see cref="IEventSubscription.Id"/> of the failing subscription (for
/// <see cref="EventFailureStage.Dispatch"/>, the one whose handler was being timed); <see langword="null"/> for
/// <see cref="EventFailureStage.BackgroundRaise"/> and <see cref="EventFailureStage.Disposal"/>.</param>
/// <param name="Call">The call being handled; <see langword="null"/> for hook, background and disposal failures.</param>
/// <example>
/// <code>
/// EventingOptions.Default = new EventingOptions
/// {
///     DefaultExceptionHandler = failure => logger.LogError(failure.Exception, "{Event} {Stage} failed (call {CallId})",
///         failure.EventName, failure.Stage, failure.Call?.Metadata.EventCallId),
/// };
/// </code>
/// </example>
public sealed record EventHandlerFailure(Exception Exception, EventFailureStage Stage, string EventName, Guid? SubscriptionId, EventCall? Call);
