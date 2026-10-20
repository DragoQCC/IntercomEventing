using System.Diagnostics;

namespace IntercomEventing;

/// <summary>
/// The subscription and dispatch engine shared by every event kind. An event is long-lived and raised many times;
/// each raise creates one <typeparamref name="TCall"/> that all handlers of that raise receive.
/// </summary>
/// <remarks>
/// <para>Ways to subscribe, all type-safe (the call type comes from the event, so you never name it):</para>
/// <list type="bullet">
/// <item><description><c>evt.Raised += handler</c> / <c>evt.Raised -= handler</c>: a native .NET event, so it works on get-only
/// properties, readonly fields and interface members. <c>+=</c> gives no handle and takes no hooks; <c>-=</c> starts the
/// <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> hook of the subscription it removes, if it has one, in the
/// background.</description></item>
/// <item><description><see cref="Subscribe(Func{TCall, Task}, Action{Exception, TCall}?)"/>: registers immediately and returns a
/// handle.</description></item>
/// <item><description><see cref="SubscribeAsync(Func{TCall, Task}, SubscriptionOptions{TCall}?, CancellationToken)"/>: returns a
/// handle and runs lifecycle hooks.</description></item>
/// </list>
/// <para><c>evt += handler</c> directly on the event does not compile; write <c>evt.Raised += handler</c>.</para>
/// <para>The first handler of a raise starts inline on the raising thread, and so does every handler until the dispatcher has
/// to wait (see <see cref="DispatchMode"/>); handlers started after that run without the raiser's
/// <see cref="SynchronizationContext"/>. UI code that needs its context should dispatch back to it.</para>
/// <para>Concurrent raises of one event are not serialized: they are delivered concurrently and their handlers may
/// interleave. Each raise still has its own immutable call.</para>
/// <para>A raise made from inside a handler (of this event or another) is delivered inline, so it can reach the subscribers
/// after that handler before the raise that triggered it does, even when every raise is awaited. In
/// <see cref="DispatchMode.Parallel"/> mode that happens when the handler raises before its first <c>await</c>; in
/// <see cref="DispatchMode.Sequential"/> mode it always happens. When subscribers need a reply after the message it answers,
/// raise the reply after the triggering raise has returned, or order calls by <see cref="EventMetadata.RaisedAt"/> (which
/// can tie; <see cref="EventMetadata.EventCallId"/> gives no order within one millisecond).</para>
/// <para>Events implement <see cref="IAsyncDisposable"/> only, because disposal waits for handlers and hooks; see
/// <see cref="DisposeAsync"/>.</para>
/// <para>Derive from this class only to build a new event kind; use <see cref="IntercomEvent{TPayload}"/> or one of the
/// specialized kinds for everyday events. A kind raises through <see cref="DispatchAsync{TState}"/>, and starts background
/// work that nobody awaits (a timer loop, a delayed raise) with <see cref="StartWithoutCallerContext"/>, so that the work
/// does not carry the context of a handler that happened to call the kind's public method.</para>
/// </remarks>
/// <typeparam name="TCall">The call type delivered to handlers.</typeparam>
/// <example>
/// <code>
/// // a custom event kind: raise-time data goes into the call, never into fields read later
/// public sealed class CounterEvent : IntercomEventBase&lt;EventCall&lt;int&gt;&gt;
/// {
///     private int _count;
///
///     public CounterEvent(EventingOptions? options = null)
///         : base(options)
///     {
///     }
///
///     public Task IncrementAsync(CancellationToken cancellationToken = default)
///     {
///         var newCount = Interlocked.Increment(ref _count);
///         return DispatchAsync(newCount, static (count, metadata, token) => new EventCall&lt;int&gt;(count, metadata, token), caller: this, cancellationToken);
///     }
/// }
/// </code>
/// </example>
public abstract partial class IntercomEventBase<TCall> : IIntercomEvent<TCall>, IAsyncDisposable
    where TCall : EventCall
{
    //extra wait beyond StartNextEventHandlerAfter so a handler that takes exactly the configured time is not moved to the background
    private static readonly TimeSpan TimeoutGrace = TimeSpan.FromMilliseconds(50);

    private readonly Lock _subscriptionLock = new();
    private readonly EventingOptions? _ownOptions;

    //cancelled (never disposed, it owns no timer) when the event is disposed so late readers of the token do not throw
    private readonly CancellationTokenSource _disposalCancellation = new();

    //slow handlers that outlived StartNextEventHandlerAfter and unsubscribe hooks nobody awaits; DisposeAsync waits for them
    private readonly BackgroundWorkTracker _backgroundWork = new();

    //copy-on-write: writers replace the whole array under _subscriptionLock, raises read a snapshot without locking
    private EventSubscription<TCall>[] _subscriptions = [];
    private int _disposedFlag;

    /// <summary>
    /// Identifies this event instance; copied into <see cref="EventMetadata.EventId"/> of every call.
    /// </summary>
    public Guid Id { get; } = Guid.CreateVersion7();

    /// <summary>
    /// Display name used in <see cref="EventMetadata.EventName"/>. Defaults to the type name, for example
    /// <c>IntercomEvent&lt;UserCreated&gt;</c> or <c>ClientConnectedEvent</c>; set it in an object initializer to override.
    /// </summary>
    /// <example>
    /// <code>
    /// var reportReady = new IntercomEvent&lt;string&gt; { Name = "ReportReady" };
    /// </code>
    /// </example>
    public string Name
    {
        get => field ??= EventNameFormatter.Describe(GetType());
        init;
    }

    /// <summary>
    /// Number of active subscriptions.
    /// </summary>
    public int SubscriberCount => Volatile.Read(ref _subscriptions).Length;

    /// <summary>
    /// <see langword="true"/> once <see cref="DisposeAsync"/> has started.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref _disposedFlag) == 1;

    /// <summary>
    /// The dispatch settings in effect: the options passed to the constructor, else <see cref="EventingOptions.Default"/> as it
    /// is at the moment of reading.
    /// </summary>
    /// <remarks>
    /// <para>Read-only: <see cref="EventingOptions"/> is immutable and the event's options cannot be replaced. Each raise reads
    /// this once and uses that value throughout, so a change to <see cref="EventingOptions.Default"/> during a raise applies
    /// from the next raise on.</para>
    /// <para>An event kind reads it for its own background work, such as <see cref="EventingOptions.TimeProvider"/> for a timer.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var auditTrail = new IntercomEvent&lt;AuditEntry&gt;(new EventingOptions { DispatchMode = DispatchMode.Sequential });
    /// Console.WriteLine(auditTrail.Options.DispatchMode);   // Sequential
    /// </code>
    /// </example>
    public EventingOptions Options => _ownOptions ?? EventingOptions.Default;

    /// <summary>
    /// Cancelled when the event is disposed. Event kinds with their own background loops (timers, debounce) observe it.
    /// </summary>
    protected CancellationToken DisposalToken => _disposalCancellation.Token;

    //tasks DisposeAsync would currently wait for (slow handlers and unsubscribe hooks); lets tests observe a handler moving to the background
    internal int BackgroundWorkCount => _backgroundWork.Count;

    /// <summary>
    /// Native .NET event: <c>evt.Raised += handler</c> subscribes, <c>evt.Raised -= handler</c> removes the most recently added
    /// subscription of an equal delegate.
    /// </summary>
    /// <remarks>
    /// <para>Because this is an <c>event</c> member, <c>+=</c> calls its accessor instead of assigning, so it compiles wherever the
    /// event object is reachable: get-only and interface properties, readonly fields, injected services.</para>
    /// <para>An accessor returns nothing and cannot be awaited, so <c>+=</c> gives no handle and takes no hooks, and <c>-=</c> starts an
    /// <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> hook in the background. A lambda written again is a different delegate
    /// and removes nothing; keep the delegate instance or use a method group.</para>
    /// <para>A combined delegate (<c>a + b</c>) becomes one subscription per target, so each gets its own timeout and exception
    /// routing. It is added all-or-nothing: if any part cannot be added, none is, and a raise sees either every part or none.
    /// <c>-=</c> with a combined delegate removes each of its parts (or a subscription made from exactly that combined delegate
    /// with <c>Subscribe</c>). Both are atomic: concurrent <c>+=</c> and <c>-=</c> calls never lose or double up a subscription.
    /// A raise that is already delivering when <c>-=</c> runs can still reach the parts whose handlers it started before the
    /// removal, and skips the rest.</para>
    /// <para><c>+=</c> throws <see cref="ObjectDisposedException"/> after disposal and <see cref="InvalidOperationException"/> when
    /// <see cref="EventingOptions.AllowMultipleSubscribers"/> is <see langword="false"/> and the event would get a second subscriber.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// registry.UserCreated.Raised += OnUserCreatedAsync;   // Task OnUserCreatedAsync(EventCall&lt;UserCreated&gt; call)
    /// registry.UserCreated.Raised -= OnUserCreatedAsync;
    /// </code>
    /// </example>
    public event Func<TCall, Task> Raised
    {
        add
        {
            //native events accept += null as a no-op
            if (value is null)
            {
                return;
            }
            var subscriptionPerTarget = CreateSubscriptionPerTarget(value);
            RegisterNewSubscriptions(subscriptionPerTarget);
        }
        remove
        {
            //native events accept -= null as a no-op
            if (value is null)
            {
                return;
            }
            _ = RemoveLatestSubscriptionsOf(value);
        }
    }

    /// <summary>
    /// Creates the event.
    /// </summary>
    /// <param name="options">Dispatch settings for this event; <see langword="null"/> uses <see cref="EventingOptions.Default"/> at each raise.</param>
    protected IntercomEventBase(EventingOptions? options)
    {
        _ownOptions = options;
    }

    /// <summary>
    /// Subscribes <paramref name="handler"/> and returns a handle that removes it when disposed.
    /// </summary>
    /// <remarks>
    /// A combined delegate (<c>a + b</c>) stays one subscription with one handle, exception handler and timeout. Every part
    /// still runs, a part that throws does not stop the later parts, and each part's exception is routed. Use
    /// <c>Raised +=</c> to give each part its own subscription.
    /// </remarks>
    /// <param name="handler">Called once per raise with that raise's call.</param>
    /// <param name="onException">Receives each exception thrown by this handler; when omitted, <see cref="EventingOptions.DefaultExceptionHandler"/> is used.</param>
    /// <returns>The subscription handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="InvalidOperationException"><see cref="EventingOptions.AllowMultipleSubscribers"/> is <see langword="false"/> and the event already has a subscriber.</exception>
    /// <example>
    /// <code>
    /// var subscription = registry.UserCreated.Subscribe(OnUserCreatedAsync,
    ///     onException: (exception, call) => logger.LogError(exception, "Failed for {User}", call.Payload.Name));
    /// // later
    /// subscription.Dispose();
    /// </code>
    /// </example>
    public EventSubscription<TCall> Subscribe(Func<TCall, Task> handler, Action<Exception, TCall>? onException = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new EventSubscription<TCall>(this, RunEachPartSeparately(handler), handler, onException, onUnsubscribed: null);
        RegisterNewSubscriptions([subscription]);
        return subscription;
    }

    /// <summary>
    /// Subscribes a synchronous <paramref name="handler"/> and returns a handle that removes it when disposed.
    /// </summary>
    /// <remarks>
    /// <para>An <c>async void</c> method group also converts to <see cref="Action{T}"/>, and its exceptions then bypass exception
    /// routing. Pass asynchronous handlers as <see cref="Func{T, TResult}"/> returning <see cref="Task"/>.</para>
    /// <para>A combined delegate (<c>a + b</c>) stays one subscription; every part runs even when an earlier part throws, and
    /// each part's exception is routed.</para>
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
    public EventSubscription<TCall> Subscribe(Action<TCall> handler, Action<Exception, TCall>? onException = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new EventSubscription<TCall>(this, WrapSynchronousHandler(handler), handler, onException, onUnsubscribed: null);
        RegisterNewSubscriptions([subscription]);
        return subscription;
    }

    /// <summary>
    /// Subscribes <paramref name="handler"/> with lifecycle hooks and per-subscription exception handling.
    /// </summary>
    /// <remarks>
    /// <para>A combined delegate (<c>a + b</c>) stays one subscription with one handle, hook set, exception handler and
    /// timeout; every part runs even when an earlier part throws, and each part's exception is routed.</para>
    /// <para>Cancelling <paramref name="cancellationToken"/> while <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> runs
    /// stops the wait, not the hook. The hook finishes in the background: if it then succeeds,
    /// <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> runs to undo it; if it fails, the exception goes to
    /// <see cref="EventingOptions.DefaultExceptionHandler"/> as <see cref="EventFailureStage.OnSubscribed"/>. Event disposal
    /// waits for that background work.</para>
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
    ///     OnUnsubscribed = subscription => cache.FlushAsync(),
    ///     OnException = (exception, call) => logger.LogError(exception, "Failed for {User}", call.Payload.Name),
    /// });
    /// </code>
    /// </example>
    public ValueTask<EventSubscription<TCall>> SubscribeAsync(Func<TCall, Task> handler, SubscriptionOptions<TCall>? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return SubscribeWithHooksAsync(RunEachPartSeparately(handler), handler, options, cancellationToken);
    }

    /// <summary>
    /// Subscribes a synchronous <paramref name="handler"/> with lifecycle hooks and per-subscription exception handling.
    /// </summary>
    /// <remarks>
    /// Combined delegates and cancellation while <see cref="SubscriptionOptions{TCall}.OnSubscribed"/> runs behave as for
    /// <see cref="SubscribeAsync(Func{TCall, Task}, SubscriptionOptions{TCall}?, CancellationToken)"/>.
    /// </remarks>
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
    public ValueTask<EventSubscription<TCall>> SubscribeAsync(Action<TCall> handler, SubscriptionOptions<TCall>? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return SubscribeWithHooksAsync(WrapSynchronousHandler(handler), handler, options, cancellationToken);
    }

    /// <summary>
    /// Removes the most recently added subscription of <paramref name="handler"/>. Same as <c>Raised -= handler</c>, but reports
    /// whether anything was removed. An <see cref="SubscriptionOptions{TCall}.OnUnsubscribed"/> hook runs in the background.
    /// </summary>
    /// <remarks>
    /// <para>A combined delegate removes the subscription made from exactly that delegate if there is one, else the latest
    /// subscription of each of its parts.</para>
    /// <para>The removal is atomic: when a delegate is subscribed twice and two threads unsubscribe it at the same time, each
    /// call removes one subscription.</para>
    /// </remarks>
    /// <param name="handler">The delegate that was subscribed.</param>
    /// <returns><see langword="true"/> if at least one subscription was removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// bool removed = registry.UserCreated.Unsubscribe(OnUserCreatedAsync);
    /// </code>
    /// </example>
    public bool Unsubscribe(Func<TCall, Task> handler)
    {
        return RemoveLatestSubscriptionsOf(handler);
    }

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
    public bool Unsubscribe(Action<TCall> handler)
    {
        return RemoveLatestSubscriptionsOf(handler);
    }

    /// <summary>
    /// Disposes the event: cancels handler tokens, removes every subscription and waits for background handlers and hooks.
    /// </summary>
    /// <remarks>
    /// <para>The steps, in order:</para>
    /// <list type="number">
    /// <item><description>Cancels <see cref="EventCall.CancellationToken"/> for running handlers.</description></item>
    /// <item><description>Stops the event kind's own background work (a timer schedule, a pending debounce raise).</description></item>
    /// <item><description>Removes every subscription.</description></item>
    /// <item><description>Waits for handlers moved to the background after <see cref="EventingOptions.StartNextEventHandlerAfter"/>,
    /// for unsubscribe hooks started by <c>Raised -=</c>, <c>Unsubscribe</c> or <c>Dispose()</c>, and for subscribe hooks whose
    /// <c>SubscribeAsync</c> was cancelled.</description></item>
    /// <item><description>Runs and awaits the unsubscribe hooks of the subscriptions it removed.</description></item>
    /// </list>
    /// <para>Idempotent: a second call returns immediately, even while the first is still running. Raising, subscribing or
    /// <c>Raised +=</c> afterwards throws <see cref="ObjectDisposedException"/>.</para>
    /// <para>It does not wait for raises that are still delivering inline (their handlers see the cancelled token).</para>
    /// <para>It is safe to await from inside one of this event's own handlers or subscription hooks, including a handler that
    /// was moved to the background: disposal skips waiting for the calling handler (and for any handler the call is nested
    /// in), while still waiting for every other one. Work that a handler starts without awaiting it (<c>Task.Run</c>, an
    /// un-awaited task) counts as part of that handler, so disposal called from such work does not wait for that handler
    /// either. Start that work inside <c>using (ExecutionContext.SuppressFlow())</c> when disposal called from it should still
    /// wait for the handler that started it.</para>
    /// <para>User code that fails during disposal does not make it throw. The remaining steps still run, and the failure goes
    /// to <see cref="EventingOptions.DefaultExceptionHandler"/>:</para>
    /// <list type="bullet">
    /// <item><description>a throwing cancellation callback that a handler registered on <see cref="EventCall.CancellationToken"/>,
    /// or a failing event-kind shutdown: <see cref="EventFailureStage.Disposal"/>;</description></item>
    /// <item><description>a failing unsubscribe hook: <see cref="EventFailureStage.OnUnsubscribed"/>.</description></item>
    /// </list>
    /// <para>A subscription handle's <see cref="EventSubscription{TCall}.DisposeAsync"/> called while this method runs returns
    /// without waiting for the hook this method runs for it; await this method to know the hooks have finished.</para>
    /// <para>The event implements <see cref="IAsyncDisposable"/> only, so a dependency injection container that holds it must be
    /// disposed with <c>DisposeAsync</c>: a synchronous <c>ServiceProvider.Dispose()</c> throws
    /// <see cref="InvalidOperationException"/> for it. The Generic Host, ASP.NET Core and Blazor dispose their containers
    /// asynchronously; in a console app or a test, write <c>await using var provider = services.BuildServiceProvider();</c>.</para>
    /// </remarks>
    /// <returns>A task that completes when the event is disposed and the work above has finished.</returns>
    /// <example>
    /// <code>
    /// await registry.UserCreated.DisposeAsync();
    /// </code>
    /// </example>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) == 1)
        {
            return;
        }
        //the handler or hook that is calling DisposeAsync, if any; it waits for this method, so this method must not wait for it
        var callingScope = EventCallbackScope.Current;

        //both steps run user code and contain its failures, so subscriptions are always removed and hooks always run
        await CancelHandlerTokensSafelyAsync().ConfigureAwait(false);
        await StopEventKindWorkSafelyAsync().ConfigureAwait(false);

        var removedSubscriptions = RemoveAllSubscriptions();
        await _backgroundWork.WhenAllAsync(callingScope).ConfigureAwait(false);
        foreach (var subscription in removedSubscriptions)
        {
            await RunUnsubscribeHookSafelyAsync(subscription).ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Stops the event kind's own background work (timers, pending raises) when the event is disposed.
    /// </summary>
    /// <remarks>
    /// <para><see cref="DisposeAsync"/> calls it once, after <see cref="DisposalToken"/> is cancelled and before subscriptions
    /// are removed. The base implementation does nothing.</para>
    /// <para>It must not wait for handlers of this event: disposal waits for them afterwards. An exception it throws is
    /// reported as <see cref="EventFailureStage.Disposal"/> and disposal continues.</para>
    /// </remarks>
    /// <returns>A task that completes when the event kind's own work has stopped.</returns>
    /// <example>
    /// <code>
    /// protected override ValueTask OnDisposingAsync()
    /// {
    ///     // DisposalToken is already cancelled, which ends the polling loop; release the timer it waited on
    ///     _pollingTimer.Dispose();
    ///     return ValueTask.CompletedTask;
    /// }
    /// </code>
    /// </example>
    protected virtual ValueTask OnDisposingAsync()
    {
        return ValueTask.CompletedTask;
    }

    internal async ValueTask RemoveAndAwaitUnsubscribeHookAsync(EventSubscription<TCall> subscription)
    {
        if (!TryRemoveSubscription(subscription))
        {
            return;
        }
        //awaited by the caller, so a failing hook surfaces to whoever unsubscribed
        await InvokeUnsubscribeHook(subscription).ConfigureAwait(false);
    }

    internal void RemoveAndStartUnsubscribeHook(EventSubscription<TCall> subscription)
    {
        if (TryRemoveSubscription(subscription))
        {
            StartUnsubscribeHookInBackground(subscription);
        }
    }

    //Subscribe(a + b) keeps one subscription, but invoking a combined Func directly would run the parts one after another, stop
    //at the first synchronous throw and return only the last part's task; instead every part runs and every part's task is observed
    private static Func<TCall, Task> RunEachPartSeparately(Func<TCall, Task> handler)
    {
        if (handler.HasSingleTarget)
        {
            return handler;
        }
        var handlerParts = handler.GetInvocationList();
        return call => InvokeEveryPartAsync(handlerParts, call);
    }

    private static Task InvokeEveryPartAsync(Delegate[] handlerParts, TCall call)
    {
        var partTasks = new Task[handlerParts.Length];
        for (var index = 0; index < handlerParts.Length; index++)
        {
            var handlerPart = (Func<TCall, Task>)handlerParts[index];
            partTasks[index] = InvokeCatchingSynchronousThrow(handlerPart, call);
        }
        //faults with every part's exception, so each one is routed; a part's timeout is the whole subscription's timeout
        return Task.WhenAll(partTasks);
    }

    private static Func<TCall, Task> WrapSynchronousHandler(Action<TCall> handler)
    {
        if (!handler.HasSingleTarget)
        {
            //Subscribe(a + b) with Action parts: a part that throws must not stop the later parts
            var handlerParts = handler.GetInvocationList();
            return call => InvokeEverySynchronousPart(handlerParts, call);
        }
        return call =>
        {
            handler(call);
            return Task.CompletedTask;
        };
    }

    private static Task InvokeEverySynchronousPart(Delegate[] handlerParts, TCall call)
    {
        List<Exception>? partFailures = null;
        foreach (var handlerPart in handlerParts)
        {
            try
            {
                ((Action<TCall>)handlerPart)(call);
            }
            catch (Exception exception)
            {
                partFailures ??= [];
                partFailures.Add(exception);
            }
        }
        if (partFailures is null)
        {
            return Task.CompletedTask;
        }
        //one faulted task that carries every part's exception, so each is routed like the parts of an asynchronous handler
        var failedInvocation = new TaskCompletionSource();
        failedInvocation.SetException(partFailures);
        return failedInvocation.Task;
    }

    //a hook that returns null is treated as finished, like a handler that does
    private static Task InvokeUnsubscribeHook(EventSubscription<TCall> subscription)
    {
        return subscription.OnUnsubscribed?.Invoke(subscription) ?? Task.CompletedTask;
    }

    private EventSubscription<TCall>[] CreateSubscriptionPerTarget(Func<TCall, Task> handler)
    {
        //a combined delegate (a + b) becomes one subscription per target, each removable on its own like native multicast parts
        var targets = handler.GetInvocationList();
        var subscriptions = new EventSubscription<TCall>[targets.Length];
        for (var index = 0; index < targets.Length; index++)
        {
            var singleHandler = (Func<TCall, Task>)targets[index];
            subscriptions[index] = new EventSubscription<TCall>(this, singleHandler, singleHandler, exceptionHandler: null, onUnsubscribed: null);
        }
        return subscriptions;
    }

    //caller holds _subscriptionLock, or calls it early to fail before running an OnSubscribed hook that would be wasted
    private void ThrowIfSubscribersCannotBeAdded(int numberOfSubscribersToAdd)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (Options.AllowMultipleSubscribers)
        {
            return;
        }
        var subscriberCountAfterAdding = Volatile.Read(ref _subscriptions).Length + numberOfSubscribersToAdd;
        if (subscriberCountAfterAdding > 1)
        {
            throw new InvalidOperationException($"{Name} allows only one subscriber (EventingOptions.AllowMultipleSubscribers is false).");
        }
    }

    //registers subscriptions nobody else has seen yet, all or none: the checks run before anything becomes visible to raises
    private void RegisterNewSubscriptions(ReadOnlySpan<EventSubscription<TCall>> newSubscriptions)
    {
        lock (_subscriptionLock)
        {
            ThrowIfSubscribersCannotBeAdded(newSubscriptions.Length);
            foreach (var subscription in newSubscriptions)
            {
                var activated = subscription.TryActivate();
                Debug.Assert(activated, "A subscription that no caller has seen yet is always pending.");
            }
            Volatile.Write(ref _subscriptions, [.. _subscriptions, .. newSubscriptions]);
        }
    }

    //makes a pending subscription visible to raises; false when its handle was disposed while OnSubscribed was still running
    private bool TryRegisterPendingSubscription(EventSubscription<TCall> subscription)
    {
        lock (_subscriptionLock)
        {
            ThrowIfSubscribersCannotBeAdded(1);
            if (!subscription.TryActivate())
            {
                return false;
            }
            Volatile.Write(ref _subscriptions, [.. _subscriptions, subscription]);
            return true;
        }
    }

    private async ValueTask<EventSubscription<TCall>> SubscribeWithHooksAsync(Func<TCall, Task> handler, Delegate registeredHandler,
        SubscriptionOptions<TCall>? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfSubscribersCannotBeAdded(1);
        var subscription = new EventSubscription<TCall>(this, handler, registeredHandler, options?.OnException, options?.OnUnsubscribed);
        var onSubscribed = options?.OnSubscribed;
        if (onSubscribed is not null)
        {
            //runs before the subscription is registered: no call reaches the handler until the hook finished,
            //and a hook that throws or is cancelled leaves nothing behind
            await RunSubscribeHookAsync(onSubscribed, subscription, cancellationToken).ConfigureAwait(false);
        }

        bool registered;
        try
        {
            registered = TryRegisterPendingSubscription(subscription);
        }
        catch (Exception) when (onSubscribed is not null)
        {
            //the event was disposed, or another subscriber won AllowMultipleSubscribers=false, while the hook ran: undo the hook first.
            //Without an OnSubscribed hook there is nothing to undo, and OnUnsubscribed must not run for a subscription that never existed
            await RunUnsubscribeHookSafelyAsync(subscription).ConfigureAwait(false);
            throw;
        }
        if (!registered)
        {
            //only reachable through OnSubscribed: the handle it received was disposed before the hook finished
            await RunUnsubscribeHookSafelyAsync(subscription).ConfigureAwait(false);
        }
        return subscription;
    }

    //completes when the hook finished (throwing its exception), or throws OperationCanceledException when the caller stops waiting
    private async Task RunSubscribeHookAsync(Func<EventSubscription<TCall>, Task> onSubscribed, EventSubscription<TCall> subscription,
        CancellationToken cancellationToken)
    {
        //the hook gets its own scope: if the caller stops waiting, the hook becomes background work that DisposeAsync waits for,
        //and DisposeAsync awaited from inside the hook must then not wait for the hook itself
        var hookScope = EventCallbackScope.CreateNested();
        Task hookTask;
        using (EventCallbackScope.Enter(hookScope))
        {
            //a hook that returns null is treated as finished, like a handler that does
            hookTask = onSubscribed(subscription) ?? Task.CompletedTask;
        }

        //SuppressThrowing: this wait only decides whether the caller stopped waiting; the hook's own outcome is read from hookTask
        await hookTask.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!hookTask.IsCompleted)
        {
            //the caller's token fired first; the hook keeps running, so its outcome is handled in the background
            subscription.AbandonIfPending();
            Task abandonedHookWork;
            using (EventCallbackScope.Enter(hookScope))
            {
                abandonedHookWork = FinishAbandonedSubscribeHookAsync(hookTask, subscription);
            }
            _backgroundWork.Track(abandonedHookWork, hookScope);
            throw new OperationCanceledException(cancellationToken);
        }
        //propagates the hook's own exception or cancellation to the caller of SubscribeAsync
        await hookTask.ConfigureAwait(false);
    }

    //never throws: the caller of SubscribeAsync stopped waiting, so nobody else will see how the hook ended
    private async Task FinishAbandonedSubscribeHookAsync(Task hookTask, EventSubscription<TCall> subscription)
    {
        try
        {
            await hookTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (hookTask.IsCanceled)
        {
            //a cancelled hook, like one that SubscribeAsync awaited, leaves nothing to undo
            return;
        }
        catch (Exception exception)
        {
            var failure = new EventHandlerFailure(exception, EventFailureStage.OnSubscribed, Name, subscription.Id, Call: null);
            RouteUnobservedFailure(failure, Options);
            return;
        }
        //the hook succeeded after its caller gave up; the subscription was never registered, so undo the hook
        await RunUnsubscribeHookSafelyAsync(subscription).ConfigureAwait(false);
    }

    private bool TryRemoveSubscription(EventSubscription<TCall> subscription)
    {
        lock (_subscriptionLock)
        {
            var currentSubscriptions = _subscriptions;
            var index = Array.IndexOf(currentSubscriptions, subscription);
            if (index < 0)
            {
                //not registered yet (OnSubscribed still running) or already removed; a pending one must never become active
                subscription.AbandonIfPending();
                return false;
            }
            Volatile.Write(ref _subscriptions, [.. currentSubscriptions.AsSpan(0, index), .. currentSubscriptions.AsSpan(index + 1)]);
            return subscription.TryDeactivate();
        }
    }

    //shared by Raised -= and Unsubscribe(handler). The search and the removal happen under one lock, so two callers removing
    //the same delegate at the same time each remove a different subscription, like -= on a native field-like event
    private bool RemoveLatestSubscriptionsOf(Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        List<EventSubscription<TCall>> removedSubscriptions;
        lock (_subscriptionLock)
        {
            removedSubscriptions = RemoveLatestSubscriptionsOfLocked(handler);
        }
        //unsubscribe hooks are user code, so they start only after the lock is released
        foreach (var subscription in removedSubscriptions)
        {
            StartUnsubscribeHookInBackground(subscription);
        }
        return removedSubscriptions.Count > 0;
    }

    //caller holds _subscriptionLock. A subscription made from exactly this delegate goes first (Subscribe(a + b) keeps a
    //combined delegate as one subscription); otherwise the latest subscription of each part goes (Raised += split it per part)
    private List<EventSubscription<TCall>> RemoveLatestSubscriptionsOfLocked(Delegate handler)
    {
        var remainingSubscriptions = new List<EventSubscription<TCall>>(_subscriptions);
        var removedSubscriptions = new List<EventSubscription<TCall>>();
        var removedExactMatch = TryMoveLatestMatch(handler, remainingSubscriptions, removedSubscriptions);
        var removeEachPart = !removedExactMatch && !handler.HasSingleTarget;
        if (removeEachPart)
        {
            var handlerParts = handler.GetInvocationList();
            //walk backwards so the parts are removed in the order native multicast removal would
            for (var index = handlerParts.Length - 1; index >= 0; index--)
            {
                TryMoveLatestMatch(handlerParts[index], remainingSubscriptions, removedSubscriptions);
            }
        }
        if (removedSubscriptions.Count == 0)
        {
            return removedSubscriptions;
        }

        //one write: a raise that takes its snapshot after it sees every part removed, one that took it before sees every part.
        //That covers the snapshot only. A raise already delivering skips each removed part whose handler it has not started
        //yet (InvokeSubscriptionAsync checks IsActive), so it can still reach some parts of a combined delegate and skip the rest
        Volatile.Write(ref _subscriptions, remainingSubscriptions.ToArray());
        foreach (var subscription in removedSubscriptions)
        {
            var deactivated = subscription.TryDeactivate();
            Debug.Assert(deactivated, "A registered subscription stays active until it is removed under the lock.");
        }
        return removedSubscriptions;
    }

    //moves the most recent subscription of an equal delegate from remaining to removed; searching from the end matches native
    //multicast delegate removal, which removes the most recent duplicate first
    private static bool TryMoveLatestMatch(Delegate handler, List<EventSubscription<TCall>> remainingSubscriptions,
        List<EventSubscription<TCall>> removedSubscriptions)
    {
        for (var index = remainingSubscriptions.Count - 1; index >= 0; index--)
        {
            var subscription = remainingSubscriptions[index];
            if (subscription.RegisteredHandler.Equals(handler))
            {
                remainingSubscriptions.RemoveAt(index);
                removedSubscriptions.Add(subscription);
                return true;
            }
        }
        return false;
    }

    private EventSubscription<TCall>[] RemoveAllSubscriptions()
    {
        lock (_subscriptionLock)
        {
            var removedSubscriptions = _subscriptions;
            Volatile.Write(ref _subscriptions, []);
            foreach (var subscription in removedSubscriptions)
            {
                subscription.TryDeactivate();
            }
            return removedSubscriptions;
        }
    }

    private void StartUnsubscribeHookInBackground(EventSubscription<TCall> subscription)
    {
        if (subscription.OnUnsubscribed is null)
        {
            return;
        }
        //the hook gets its own scope so DisposeAsync awaited from inside it does not wait for the hook itself
        var hookScope = EventCallbackScope.CreateNested();
        Task hookTask;
        using (EventCallbackScope.Enter(hookScope))
        {
            hookTask = RunUnsubscribeHookSafelyAsync(subscription);
        }
        _backgroundWork.Track(hookTask, hookScope);
    }

    //never throws: nobody awaits a background hook, so its failure can only go to the global handler; there is no call to report
    private async Task RunUnsubscribeHookSafelyAsync(EventSubscription<TCall> subscription)
    {
        if (subscription.OnUnsubscribed is null)
        {
            return;
        }
        try
        {
            await InvokeUnsubscribeHook(subscription).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var failure = new EventHandlerFailure(exception, EventFailureStage.OnUnsubscribed, Name, subscription.Id, Call: null);
            RouteUnobservedFailure(failure, Options);
        }
    }

    //disposal cancels the token every call is linked to, which runs callbacks that handlers registered on it
    private async Task CancelHandlerTokensSafelyAsync()
    {
        try
        {
            await _disposalCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException callbackFailures)
        {
            //CancelAsync runs every callback and gathers the throwing ones into one AggregateException. A callback registered on
            //the token of a raise that had its own raiser token sits on that raise's linked source, which gathers its callbacks'
            //failures into a nested AggregateException first; flattening reports each callback's own exception once
            foreach (var callbackFailure in callbackFailures.Flatten().InnerExceptions)
            {
                RouteDisposalFailure(callbackFailure);
            }
        }
        catch (Exception exception)
        {
            RouteDisposalFailure(exception);
        }
    }

    private async Task StopEventKindWorkSafelyAsync()
    {
        try
        {
            await OnDisposingAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RouteDisposalFailure(exception);
        }
    }

    private void RouteDisposalFailure(Exception exception)
    {
        var disposalFailure = new EventHandlerFailure(exception, EventFailureStage.Disposal, Name, SubscriptionId: null, Call: null);
        RouteUnobservedFailure(disposalFailure, Options);
    }
}
