namespace IntercomEventing;

/// <summary>
/// A subscription handle without its call type, for code that stores subscriptions to different events together.
/// </summary>
/// <example>
/// <code>
/// private readonly List&lt;IEventSubscription&gt; _subscriptions = [];
///
/// _subscriptions.Add(registry.UserCreated.Subscribe(OnUserCreatedAsync));
/// _subscriptions.Add(monitor.PowerChanged.Subscribe(OnPowerChangedAsync));
///
/// foreach (var subscription in _subscriptions)
/// {
///     await subscription.DisposeAsync();
/// }
/// </code>
/// </example>
public interface IEventSubscription : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Identifies this subscription; repeated in <see cref="EventHandlerFailure.SubscriptionId"/>.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// <see langword="true"/> while the handler receives calls: from registration (after any OnSubscribed hook) until the
    /// subscription is removed by any route (handle, <c>Raised -=</c>, <c>Unsubscribe</c> or event disposal).
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Removes the subscription and awaits its unsubscribe hook. Calling it again does nothing.
    /// </summary>
    /// <remarks>
    /// <para>The subscription is removed before the hook runs, so an exception thrown by the hook reaches the caller of this
    /// method (and of <c>await using</c>) with the subscription already gone.</para>
    /// <para>Only the call that removes the subscription waits for the hook. When another route removed it first (a synchronous
    /// <c>Dispose()</c>, <c>Raised -=</c>, <c>Unsubscribe(handler)</c>, or the event's own disposal), this call returns at once,
    /// possibly while that route is still running the hook. Event disposal waits for every hook, so await the event's
    /// <c>DisposeAsync</c> when you need to know they have finished. Waiting here instead could deadlock: a handler that
    /// unsubscribes itself while the event is being disposed would wait for a hook that disposal runs only after that
    /// handler finished.</para>
    /// </remarks>
    /// <returns>A task that completes when the subscription is removed and, if this call removed it, its hook has finished.</returns>
    /// <example>
    /// <code>
    /// await subscription.UnsubscribeAsync();   // OnUnsubscribed has finished when this call removed the subscription
    /// </code>
    /// </example>
    ValueTask UnsubscribeAsync();
}

/// <summary>
/// The handle returned by <see cref="IntercomEventBase{TCall}.Subscribe(Func{TCall, Task}, Action{Exception, TCall}?)"/> and
/// <see cref="IntercomEventBase{TCall}.SubscribeAsync(Func{TCall, Task}, SubscriptionOptions{TCall}?, CancellationToken)"/>.
/// Dispose it (or call <see cref="UnsubscribeAsync"/>) to stop receiving calls.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><see cref="DisposeAsync"/> and <see cref="UnsubscribeAsync"/> await the <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/>
/// hook when they are the route that removes the subscription; after another route removed it they return at once.</description></item>
/// <item><description><see cref="Dispose"/> removes the handler immediately and starts the hook without waiting for it. It never blocks, which keeps it safe in Blazor WebAssembly.</description></item>
/// <item><description>Every removal route is idempotent, and the hook runs at most once.</description></item>
/// <item><description>There is no finalizer: like a native .NET event, a subscription you never dispose lives as long as the event.</description></item>
/// </list>
/// </remarks>
/// <typeparam name="TCall">The call type of the subscribed event.</typeparam>
/// <example>
/// <code>
/// await using var subscription = registry.UserCreated.Subscribe(OnUserCreatedAsync);
/// await registry.CreateAsync(new UserCreated("Ada", "ada@example.com"));
/// // the handler is removed when the using scope ends
/// </code>
/// </example>
public sealed class EventSubscription<TCall> : IEventSubscription
    where TCall : EventCall
{
    //Pending -> Active when registered (after OnSubscribed), Active -> Removed once; Interlocked so every removal route runs the hook at most once
    private const int PendingState = 0;
    private const int ActiveState = 1;
    private const int RemovedState = 2;

    //the event that created this handle; kept privately so the public Event property cannot be used to raise
    private readonly IntercomEventBase<TCall> _owner;
    private int _state = PendingState;

    /// <inheritdoc />
    public Guid Id { get; } = Guid.CreateVersion7();

    /// <summary>
    /// The event this subscription belongs to, as its subscribe-only view.
    /// </summary>
    public IIntercomEvent<TCall> Event => _owner;

    /// <inheritdoc />
    public bool IsActive => Volatile.Read(ref _state) == ActiveState;

    //the handler normalized to Func<TCall, Task>; synchronous Action handlers are wrapped and combined delegates run part by part
    internal Func<TCall, Task> Handler { get; }

    //the delegate exactly as the subscriber passed it, used by -= and Unsubscribe(handler) to find this subscription
    internal Delegate RegisteredHandler { get; }

    internal Action<Exception, TCall>? ExceptionHandler { get; }

    internal Func<EventSubscription<TCall>, Task>? OnUnsubscribed { get; }

    internal EventSubscription(IntercomEventBase<TCall> subscribedEvent, Func<TCall, Task> handler, Delegate registeredHandler,
        Action<Exception, TCall>? exceptionHandler, Func<EventSubscription<TCall>, Task>? onUnsubscribed)
    {
        _owner = subscribedEvent;
        Handler = handler;
        RegisteredHandler = registeredHandler;
        ExceptionHandler = exceptionHandler;
        OnUnsubscribed = onUnsubscribed;
    }

    /// <inheritdoc />
    public ValueTask UnsubscribeAsync()
    {
        return _owner.RemoveAndAwaitUnsubscribeHookAsync(this);
    }

    /// <summary>
    /// Same as <see cref="UnsubscribeAsync"/>: removes the subscription and awaits its unsubscribe hook.
    /// </summary>
    /// <remarks>
    /// <para>An exception thrown by <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> propagates to the caller; the
    /// subscription has already been removed by then.</para>
    /// <para>When another route already removed the subscription (<see cref="Dispose"/>, <c>Raised -=</c>,
    /// <c>Unsubscribe(handler)</c> or the event's disposal), this returns at once without waiting for the hook that route
    /// runs; see <see cref="UnsubscribeAsync"/>.</para>
    /// </remarks>
    /// <returns>A task that completes when the subscription is removed and, if this call removed it, its hook has finished.</returns>
    /// <example>
    /// <code>
    /// await using var subscription = await registry.UserCreated.SubscribeAsync(OnUserCreatedAsync, new()
    /// {
    ///     OnUnsubscribed = subscription => cache.FlushAsync(),
    /// });
    /// // DisposeAsync runs when the scope ends and waits for cache.FlushAsync(), unless the event was disposed first:
    /// // then the event's DisposeAsync runs the hook and is the call to await
    /// </code>
    /// </example>
    public ValueTask DisposeAsync()
    {
        return UnsubscribeAsync();
    }

    /// <summary>
    /// Removes the handler immediately. An <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> hook is started in the
    /// background; its exceptions go to <see cref="EventingOptions.DefaultExceptionHandler"/> and event disposal waits for it.
    /// Never blocks.
    /// </summary>
    /// <example>
    /// <code>
    /// using var subscription = registry.UserCreated.Subscribe(OnUserCreatedAsync);
    /// // Dispose runs when the scope ends; the handler receives no later calls
    /// </code>
    /// </example>
    public void Dispose()
    {
        _owner.RemoveAndStartUnsubscribeHook(this);
    }

    //true only for the caller that registers it
    internal bool TryActivate()
    {
        return Interlocked.CompareExchange(ref _state, ActiveState, PendingState) == PendingState;
    }

    //returns true only for the first caller, so the unsubscribe hook runs exactly once
    internal bool TryDeactivate()
    {
        return Interlocked.CompareExchange(ref _state, RemovedState, ActiveState) == ActiveState;
    }

    //a handle disposed while its OnSubscribed hook still runs must never become active
    internal void AbandonIfPending()
    {
        Interlocked.CompareExchange(ref _state, RemovedState, PendingState);
    }
}
