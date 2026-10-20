namespace IntercomEventing;

/// <summary>
/// Optional lifecycle hooks and exception handling for one subscription, passed to
/// <see cref="IntercomEventBase{TCall}.SubscribeAsync(Func{TCall, Task}, SubscriptionOptions{TCall}?, CancellationToken)"/>.
/// </summary>
/// <typeparam name="TCall">The event's call type; inferred when you write <c>new() { ... }</c> at the call site.</typeparam>
/// <example>
/// <code>
/// var subscription = await registry.UserCreated.SubscribeAsync(SendWelcomeMailAsync, new()
/// {
///     OnSubscribed = async subscription => await mailQueue.OpenAsync(),
///     OnUnsubscribed = async subscription => await mailQueue.FlushAsync(),
///     OnException = (exception, call) => logger.LogError(exception, "Welcome mail failed for {User}", call.Payload.Name),
/// });
/// </code>
/// </example>
public sealed class SubscriptionOptions<TCall>
    where TCall : EventCall
{
    /// <summary>
    /// Runs before the handler is registered; <c>SubscribeAsync</c> completes after it. No call reaches the handler while
    /// this hook runs. If it throws or its task is cancelled, nothing is registered, <see cref="OnUnsubscribed"/> does not run
    /// and the exception propagates to the caller of <c>SubscribeAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>If the handle passed to the hook is disposed before the hook finishes, the subscription never becomes active and
    /// <see cref="OnUnsubscribed"/> runs once the hook has finished.</para>
    /// <para>If registration fails after the hook succeeded (the event was disposed meanwhile, or another subscriber took the
    /// only slot when <see cref="EventingOptions.AllowMultipleSubscribers"/> is <see langword="false"/>), <see cref="OnUnsubscribed"/>
    /// runs to undo the hook and the exception then propagates.</para>
    /// <para>Cancelling the token passed to <c>SubscribeAsync</c> stops the wait, not the hook: <c>SubscribeAsync</c> throws
    /// <see cref="OperationCanceledException"/> and nothing is registered. When the hook later succeeds,
    /// <see cref="OnUnsubscribed"/> runs to undo it; when it fails, the exception goes to
    /// <see cref="EventingOptions.DefaultExceptionHandler"/> as <see cref="EventFailureStage.OnSubscribed"/>.</para>
    /// <para>A hook that returns <see langword="null"/> is treated as finished.</para>
    /// </remarks>
    public Func<EventSubscription<TCall>, Task>? OnSubscribed { get; init; }

    /// <summary>
    /// Runs once when the subscription is removed, by whichever route removes it.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Awaited by <see cref="EventSubscription{TCall}.UnsubscribeAsync"/> and
    /// <see cref="EventSubscription{TCall}.DisposeAsync"/>, whose callers receive its exception. Only the call that removes the
    /// subscription waits: once another route removed it, these return without waiting for the hook.</description></item>
    /// <item><description>Awaited by event disposal; a failure goes to <see cref="EventingOptions.DefaultExceptionHandler"/>
    /// as <see cref="EventFailureStage.OnUnsubscribed"/>.</description></item>
    /// <item><description>Started in the background, not awaited, by <c>Raised -=</c>,
    /// <see cref="IntercomEventBase{TCall}.Unsubscribe(Func{TCall, Task})"/> and <see cref="EventSubscription{TCall}.Dispose"/>;
    /// a failure goes to <see cref="EventingOptions.DefaultExceptionHandler"/> and event disposal waits for the hook.</description></item>
    /// <item><description>Also runs to undo <see cref="OnSubscribed"/> when the subscription is never registered (see there).
    /// Without an <see cref="OnSubscribed"/> hook it never runs for a subscription that was not registered.</description></item>
    /// <item><description>A hook that returns <see langword="null"/> is treated as finished.</description></item>
    /// </list>
    /// </remarks>
    public Func<EventSubscription<TCall>, Task>? OnUnsubscribed { get; init; }

    /// <summary>
    /// Receives each exception thrown by this subscription's handler, together with the call being handled. Takes precedence
    /// over <see cref="EventingOptions.DefaultExceptionHandler"/>. An exception thrown by this handler itself is contained.
    /// </summary>
    /// <remarks>
    /// A handler that ends with <see cref="OperationCanceledException"/> after its <see cref="EventCall.CancellationToken"/>
    /// was cancelled counts as cancelled and is not reported here.
    /// </remarks>
    public Action<Exception, TCall>? OnException { get; init; }
}
