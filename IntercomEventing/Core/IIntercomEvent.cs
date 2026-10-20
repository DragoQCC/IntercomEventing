namespace IntercomEventing;

/// <summary>
/// The subscribe-only view of an event: everything a subscriber needs, and nothing that lets it raise or dispose the event.
/// </summary>
/// <remarks>
/// <para>Every event implements this interface. A producer that wants only itself to raise an event keeps the event in a private
/// field and exposes it through this interface; dependency injection and the Blazor helpers accept it too.</para>
/// <para>The view is the event object itself, typed as this interface: it limits what subscribers see, not what they can reach.
/// Code that casts it back to the event type can raise or dispose the event, as with any interface over a mutable object.
/// Expose a wrapper of your own when that must be impossible.</para>
/// </remarks>
/// <typeparam name="TCall">The call type delivered to handlers, for example <c>EventCall&lt;UserCreated&gt;</c>.</typeparam>
/// <example>
/// <code>
/// public sealed class UserRegistry
/// {
///     private readonly IntercomEvent&lt;UserCreated&gt; _userCreated = new();
///
///     public IIntercomEvent&lt;EventCall&lt;UserCreated&gt;&gt; UserCreated => _userCreated;
/// }
///
/// registry.UserCreated.Raised += OnUserCreatedAsync;
/// </code>
/// </example>
public interface IIntercomEvent<TCall> where TCall : EventCall
{
    /// <summary>
    /// Identifies the event instance; repeated in every call's <see cref="EventMetadata.EventId"/>.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// Display name of the event, repeated in every call's <see cref="EventMetadata.EventName"/>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Number of active subscriptions.
    /// </summary>
    int SubscriberCount { get; }

    /// <summary>
    /// The dispatch settings in effect: the options passed to the event's constructor, else <see cref="EventingOptions.Default"/>
    /// as it is at the moment of reading.
    /// </summary>
    /// <remarks>
    /// <para>Read-only: <see cref="EventingOptions"/> is immutable and an event's options cannot be replaced. For an event without
    /// options of its own, a later change to <see cref="EventingOptions.Default"/> shows at the next read.</para>
    /// <para>Code that handles an event's failures outside its dispatch, such as a UI integration, reads it to report them to
    /// the same <see cref="EventingOptions.DefaultExceptionHandler"/> the event uses.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var failure = new EventHandlerFailure(exception, EventFailureStage.Handler, registry.UserCreated.Name, subscription.Id, call);
    /// registry.UserCreated.Options.DefaultExceptionHandler?.Invoke(failure);
    /// </code>
    /// </example>
    EventingOptions Options { get; }

    /// <summary>
    /// Native .NET event accessor: <c>evt.Raised += handler</c> subscribes and <c>evt.Raised -= handler</c> removes the most recent
    /// subscription of an equal delegate. <c>+=</c> gives no handle and takes no hooks; use
    /// <see cref="Subscribe(Func{TCall, Task}, Action{Exception, TCall}?)"/> or
    /// <see cref="SubscribeAsync(Func{TCall, Task}, SubscriptionOptions{TCall}?, CancellationToken)"/> for those. <c>-=</c> starts
    /// the <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> hook of the subscription it removes, if it has one, in the
    /// background.
    /// </summary>
    /// <example>
    /// <code>
    /// registry.UserCreated.Raised += OnUserCreatedAsync;   // Task OnUserCreatedAsync(EventCall&lt;UserCreated&gt; call)
    /// registry.UserCreated.Raised -= OnUserCreatedAsync;
    /// </code>
    /// </example>
    event Func<TCall, Task> Raised;

    /// <summary>
    /// Subscribes <paramref name="handler"/> and returns a handle that removes it when disposed.
    /// </summary>
    /// <remarks>
    /// A combined delegate (<c>a + b</c>) stays one subscription; every part runs even when an earlier part throws, and each
    /// part's exception is routed. Use <c>Raised +=</c> to give each part its own subscription.
    /// </remarks>
    /// <param name="handler">Called once per raise with that raise's call.</param>
    /// <param name="onException">Receives each exception thrown by this handler; when omitted, <see cref="EventingOptions.DefaultExceptionHandler"/> is used.</param>
    /// <returns>The subscription handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="InvalidOperationException"><see cref="EventingOptions.AllowMultipleSubscribers"/> is <see langword="false"/> and the event already has a subscriber.</exception>
    /// <example>
    /// <code>
    /// using var subscription = registry.UserCreated.Subscribe(OnUserCreatedAsync);
    /// </code>
    /// </example>
    EventSubscription<TCall> Subscribe(Func<TCall, Task> handler, Action<Exception, TCall>? onException = null);

    /// <summary>
    /// Subscribes a synchronous <paramref name="handler"/> and returns a handle that removes it when disposed.
    /// </summary>
    /// <remarks>
    /// An <c>async void</c> method group also converts to <see cref="Action{T}"/>, and its exceptions then bypass exception
    /// routing; a lambda such as <c>call => { SaveAsync(call); }</c> drops the task it starts. Pass asynchronous handlers as
    /// <see cref="Func{T, TResult}"/> returning <see cref="Task"/> (an <c>async</c> lambda picks that overload).
    /// </remarks>
    /// <param name="handler">Called once per raise with that raise's call.</param>
    /// <param name="onException">Receives each exception thrown by this handler.</param>
    /// <returns>The subscription handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="InvalidOperationException"><see cref="EventingOptions.AllowMultipleSubscribers"/> is <see langword="false"/> and the event already has a subscriber.</exception>
    /// <example>
    /// <code>
    /// using var subscription = registry.UserCreated.Subscribe(call => Console.WriteLine($"Welcome {call.Payload.Name}"));
    /// </code>
    /// </example>
    EventSubscription<TCall> Subscribe(Action<TCall> handler, Action<Exception, TCall>? onException = null);

    /// <summary>
    /// Subscribes <paramref name="handler"/> with lifecycle hooks and per-subscription exception handling.
    /// </summary>
    /// <remarks>
    /// Cancelling <paramref name="cancellationToken"/> while <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> runs stops
    /// the wait, not the hook; see <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> for how the hook is then finished.
    /// </remarks>
    /// <param name="handler">Called once per raise with that raise's call.</param>
    /// <param name="options">Hooks and exception handler; <c>new() { ... }</c> infers the call type.</param>
    /// <param name="cancellationToken">Stops waiting for <see cref="SubscriptionOptions{TCall}.OnSubscribed"/>; the subscription is then not created.</param>
    /// <returns>The subscription handle, after <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> completed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="InvalidOperationException"><see cref="EventingOptions.AllowMultipleSubscribers"/> is <see langword="false"/> and the event already has a subscriber.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the call or while
    /// <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> was running.</exception>
    /// <example>
    /// <code>
    /// await using var subscription = await registry.UserCreated.SubscribeAsync(OnUserCreatedAsync, new()
    /// {
    ///     OnSubscribed = subscription => cache.WarmUpAsync(),
    ///     OnException = (exception, call) => logger.LogError(exception, "Failed for {User}", call.Payload.Name),
    /// });
    /// </code>
    /// </example>
    ValueTask<EventSubscription<TCall>> SubscribeAsync(Func<TCall, Task> handler, SubscriptionOptions<TCall>? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes a synchronous <paramref name="handler"/> with lifecycle hooks and per-subscription exception handling.
    /// </summary>
    /// <param name="handler">Called once per raise with that raise's call.</param>
    /// <param name="options">Hooks and exception handler.</param>
    /// <param name="cancellationToken">Stops waiting for <see cref="SubscriptionOptions{TCall}.OnSubscribed"/>; the subscription is then not created.</param>
    /// <returns>The subscription handle, after <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> completed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="InvalidOperationException"><see cref="EventingOptions.AllowMultipleSubscribers"/> is <see langword="false"/> and the event already has a subscriber.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the call or while
    /// <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> was running.</exception>
    /// <example>
    /// <code>
    /// var subscription = await registry.UserCreated.SubscribeAsync(call => counter.Increment(), new()
    /// {
    ///     OnUnsubscribed = subscription => counter.FlushAsync(),
    /// });
    /// </code>
    /// </example>
    ValueTask<EventSubscription<TCall>> SubscribeAsync(Action<TCall> handler, SubscriptionOptions<TCall>? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the most recently added subscription of <paramref name="handler"/>; same as <c>Raised -= handler</c> but reports
    /// whether anything was removed. An <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> hook runs in the background.
    /// </summary>
    /// <param name="handler">The delegate that was subscribed.</param>
    /// <returns><see langword="true"/> if at least one subscription was removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// bool removed = registry.UserCreated.Unsubscribe(OnUserCreatedAsync);
    /// </code>
    /// </example>
    bool Unsubscribe(Func<TCall, Task> handler);

    /// <summary>
    /// Removes the most recently added subscription of a synchronous <paramref name="handler"/>.
    /// </summary>
    /// <param name="handler">The delegate that was subscribed.</param>
    /// <returns><see langword="true"/> if at least one subscription was removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// Action&lt;EventCall&lt;UserCreated&gt;&gt; printUser = call => Console.WriteLine(call.Payload.Name);
    /// registry.UserCreated.Subscribe(printUser);
    /// registry.UserCreated.Unsubscribe(printUser);
    /// </code>
    /// </example>
    bool Unsubscribe(Action<TCall> handler);
}
