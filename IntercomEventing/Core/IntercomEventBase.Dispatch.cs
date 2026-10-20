using System.Diagnostics;

namespace IntercomEventing;

//dispatch half of IntercomEventBase: turning one raise into handler invocations
public abstract partial class IntercomEventBase<TCall>
{
    /// <summary>
    /// Raises the event: builds one call with <paramref name="createCall"/> and delivers it to every active subscription
    /// according to <see cref="Options"/>. Event kinds call this from their public raise methods.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>When there are no subscribers, the factory is not called and nothing is allocated; pass a
    /// <see langword="static"/> lambda and put the raise data in <paramref name="state"/> to keep it that way.</description></item>
    /// <item><description>Handler exceptions never propagate to the raiser; they are routed to the subscription's handler,
    /// else <see cref="EventingOptions.DefaultExceptionHandler"/>, else swallowed.</description></item>
    /// <item><description>The returned task completes when every handler finished or was moved to the background after
    /// <see cref="EventingOptions.StartNextEventHandlerAfter"/>.</description></item>
    /// <item><description>Raises are not serialized: concurrent calls deliver concurrently.</description></item>
    /// </list>
    /// </remarks>
    /// <typeparam name="TState">The raise-specific data handed to <paramref name="createCall"/>.</typeparam>
    /// <param name="state">The raise-specific data (payload, values) the call is built from. Never keep it in fields.</param>
    /// <param name="createCall">Creates the call from <paramref name="state"/> and the metadata and token supplied here.</param>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/>.</param>
    /// <param name="cancellationToken">Stops starting further handlers and is linked into <see cref="EventCall.CancellationToken"/>.</param>
    /// <returns>A task that completes when every handler finished or was moved to the background.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="createCall"/> is <see langword="null"/> (thrown synchronously).</exception>
    /// <exception cref="ObjectDisposedException">The event has been disposed (reported through the returned task).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before every handler
    /// started (reported through the returned task).</exception>
    /// <example>
    /// <code>
    /// public Task RaiseAsync(int count, object? caller = null, CancellationToken cancellationToken = default)
    /// {
    ///     return DispatchAsync(count, static (value, metadata, token) => new EventCall&lt;int&gt;(value, metadata, token), caller, cancellationToken);
    /// }
    /// </code>
    /// </example>
    protected Task DispatchAsync<TState>(TState state, EventCallFactory<TState, TCall> createCall, object? caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createCall);
        if (IsDisposed)
        {
            return Task.FromException(new ObjectDisposedException(GetType().FullName));
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var subscriptions = Volatile.Read(ref _subscriptions);
        //fast path: no subscribers means no metadata, no call, no token source and no allocation
        if (subscriptions.Length == 0)
        {
            return Task.CompletedTask;
        }

        //read once so a whole raise uses one consistent set of options even if EventingOptions.Default changes meanwhile
        var options = Options;

        //handlers get a single token that fires when either the raiser cancels or the event is disposed
        CancellationTokenSource? linkedCancellation = null;
        var callCancellationToken = _disposalCancellation.Token;
        if (cancellationToken.CanBeCanceled)
        {
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(callCancellationToken, cancellationToken);
            callCancellationToken = linkedCancellation.Token;
        }

        TCall call;
        try
        {
            var raisedAt = options.TimeProvider.GetUtcNow();
            var metadata = new EventMetadata(Id, Name, CreateEventCallId(raisedAt), raisedAt, caller);
            call = createCall(state, metadata, callCancellationToken);
        }
        catch (Exception exception)
        {
            //a failing clock or factory reaches the raiser like any other raise failure, without leaking the token source
            linkedCancellation?.Dispose();
            return Task.FromException(exception);
        }
        return DeliverAsync(subscriptions, call, options, linkedCancellation, cancellationToken);
    }

    /// <summary>
    /// Reports a failure of an event kind's own background work (a timer loop, a debounce wait) to
    /// <see cref="EventingOptions.DefaultExceptionHandler"/> as <see cref="EventFailureStage.BackgroundRaise"/>.
    /// </summary>
    /// <remarks>
    /// <para>Use it in a loop that nobody awaits, so the loop's failures are reported instead of becoming unobserved task
    /// exceptions. Handler failures are already routed by <see cref="DispatchAsync{TState}"/>; report only the loop's own.
    /// Start such a loop with <see cref="StartWithoutCallerContext"/>.</para>
    /// <para>An exception thrown by <see cref="EventingOptions.DefaultExceptionHandler"/> is contained; without a handler the
    /// failure is swallowed.</para>
    /// </remarks>
    /// <param name="exception">The failure to report.</param>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// public void StartPolling()
    /// {
    ///     _ = StartWithoutCallerContext(PollAsync);
    /// }
    ///
    /// private async Task PollAsync()
    /// {
    ///     try
    ///     {
    ///         while (await _pollingTimer.WaitForNextTickAsync(DisposalToken))
    ///         {
    ///             var reading = await _sensor.ReadAsync(DisposalToken);
    ///             await DispatchAsync(reading, static (value, metadata, token) => new EventCall&lt;double&gt;(value, metadata, token), caller: null, CancellationToken.None);
    ///         }
    ///     }
    ///     catch (OperationCanceledException) when (DisposalToken.IsCancellationRequested)
    ///     {
    ///         // the event was disposed
    ///     }
    ///     catch (Exception exception)
    ///     {
    ///         RouteBackgroundRaiseFailure(exception);
    ///     }
    /// }
    /// </code>
    /// </example>
    protected void RouteBackgroundRaiseFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var failure = new EventHandlerFailure(exception, EventFailureStage.BackgroundRaise, Name, SubscriptionId: null, Call: null);
        RouteUnobservedFailure(failure, Options);
    }

    /// <summary>
    /// Starts background work of an event kind (a timer loop, a delayed raise) without the caller's
    /// <see cref="ExecutionContext"/>, and returns the work's task.
    /// </summary>
    /// <remarks>
    /// <para>Use it for work that outlives the public method that starts it. That method is often called from inside another
    /// event's handler, and work started normally carries the handler's <see cref="AsyncLocal{T}"/> values into every later
    /// raise: its <c>Activity.Current</c> and logging scopes reach later handlers, and <see cref="DisposeAsync"/> called from
    /// the work treats it as part of that handler and does not wait for the handler. <see cref="TimedEvent{TPayload}"/> starts
    /// its schedule this way.</para>
    /// <para>The work starts inline on the calling thread, with the flow suppressed, and runs there until it first awaits
    /// something incomplete; from then on it runs in a clean context. Make sure that happens before the first raise, for
    /// example with <c>await Task.Yield()</c> or a first wait with <c>ConfigureAwait(ConfigureAwaitOptions.ForceYielding)</c>
    /// (an already completed task continues inline): handlers started while the flow is suppressed lose their own
    /// <see cref="AsyncLocal{T}"/> values at their first <c>await</c>.</para>
    /// <para>Nobody observes the returned task unless the event kind keeps it, so report the work's own failures with
    /// <see cref="RouteBackgroundRaiseFailure"/>.</para>
    /// </remarks>
    /// <param name="work">Starts the work, usually an <see langword="async"/> method of the event kind.</param>
    /// <returns>The task that <paramref name="work"/> returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// public void StartSampling()
    /// {
    ///     // the loop outlives this call, which may come from inside another event's handler
    ///     _ = StartWithoutCallerContext(SampleLoopAsync);
    /// }
    /// </code>
    /// </example>
    protected static Task StartWithoutCallerContext(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        //SuppressFlow throws when the flow is already suppressed, and then there is nothing to suppress anyway
        if (ExecutionContext.IsFlowSuppressed())
        {
            return work();
        }
        //with the flow suppressed, the work's first await captures no context, so everything after it runs in a clean one
        using (ExecutionContext.SuppressFlow())
        {
            return work();
        }
    }

    //a version 7 GUID stores milliseconds since 1970-01-01, and Guid.CreateVersion7 rejects an earlier time. A clock that
    //reports one (a FakeTimeProvider started at an arbitrary date) must not fail the raise, so its id gets the current system
    //time instead; RaisedAt keeps the clock's value either way
    private static Guid CreateEventCallId(DateTimeOffset raisedAt)
    {
        var raisedBeforeUnixEpoch = raisedAt < DateTimeOffset.UnixEpoch;
        if (raisedBeforeUnixEpoch)
        {
            return Guid.CreateVersion7();
        }
        return Guid.CreateVersion7(raisedAt);
    }

    private static void DisposeWhenHandlersFinish(CancellationTokenSource linkedCancellation, Task?[] startedHandlerTasks)
    {
        var startedHandlers = startedHandlerTasks.OfType<Task>().ToArray();
        var everyHandlerFinished = Array.TrueForAll(startedHandlers, static handlerTask => handlerTask.IsCompleted);
        if (everyHandlerFinished)
        {
            linkedCancellation.Dispose();
            return;
        }
        //handlers moved to the background still read the token, so the source lives until the last of them ends
        _ = Task.WhenAll(startedHandlers).ContinueWith(
            static (allHandlers, state) =>
            {
                //handler failures were already routed; reading Exception only marks this aggregate task as observed
                _ = allHandlers.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            linkedCancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Task StartHandler(EventSubscription<TCall> subscription, TCall call, EventCallbackScope? handlerScope)
    {
        if (handlerScope is null)
        {
            return InvokeCatchingSynchronousThrow(subscription.Handler, call);
        }
        //the handler captures the scope at its first await and keeps it, so DisposeAsync awaited from inside it can recognise it
        using (EventCallbackScope.Enter(handlerScope))
        {
            return InvokeCatchingSynchronousThrow(subscription.Handler, call);
        }
    }

    private static Task InvokeCatchingSynchronousThrow(Func<TCall, Task> handler, TCall call)
    {
        try
        {
            //a handler that returns null is treated as finished
            return handler(call) ?? Task.CompletedTask;
        }
        catch (Exception exception)
        {
            //a handler that throws before returning its task is treated like one whose task faulted
            return Task.FromException(exception);
        }
    }

    private static void RouteUnobservedFailure(EventHandlerFailure failure, EventingOptions options)
    {
        try
        {
            options.DefaultExceptionHandler?.Invoke(failure);
        }
        catch (Exception exceptionHandlerFailure)
        {
            //a throwing exception handler must not break dispatch for other subscribers
            var failureType = exceptionHandlerFailure.GetType().Name;
            Debug.WriteLine($"IntercomEventing: DefaultExceptionHandler threw {failureType}: {exceptionHandlerFailure.Message}");
        }
    }

    private async Task DeliverAsync(EventSubscription<TCall>[] subscriptions, TCall call, EventingOptions options,
        CancellationTokenSource? linkedCancellation, CancellationToken cancellationToken)
    {
        //only needed to know when the linked token source can be disposed; without one there is nothing to track
        var startedHandlerTasks = linkedCancellation is null ? null : new Task?[subscriptions.Length];
        try
        {
            var runOneAtATime = subscriptions.Length == 1 || options.DispatchMode == DispatchMode.Sequential;
            if (runOneAtATime)
            {
                await RunSequentiallyAsync(subscriptions, call, options, startedHandlerTasks, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunInParallelAsync(subscriptions, call, options, startedHandlerTasks, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (linkedCancellation is not null)
            {
                //startedHandlerTasks is created exactly when there is a linked source to dispose
                Debug.Assert(startedHandlerTasks is not null);
                DisposeWhenHandlersFinish(linkedCancellation, startedHandlerTasks);
            }
        }
    }

    private void RouteHandlerException(Exception exception, EventSubscription<TCall> subscription, TCall call, EventingOptions options)
    {
        //the raise was cancelled or the event disposed; a handler that honours the token has not failed
        var handlerHonouredCancellation = exception is OperationCanceledException && call.CancellationToken.IsCancellationRequested;
        if (handlerHonouredCancellation)
        {
            return;
        }
        if (subscription.ExceptionHandler is null)
        {
            //no subscription handler: global default, else swallowed so one failing subscriber cannot break the raise for the others
            var failure = new EventHandlerFailure(exception, EventFailureStage.Handler, Name, subscription.Id, call);
            RouteUnobservedFailure(failure, options);
            return;
        }
        try
        {
            subscription.ExceptionHandler(exception, call);
        }
        catch (Exception exceptionHandlerFailure)
        {
            var failureType = exceptionHandlerFailure.GetType().Name;
            Debug.WriteLine($"IntercomEventing: subscription exception handler threw {failureType}: {exceptionHandlerFailure.Message}");
        }
    }

    private async Task RunSequentiallyAsync(EventSubscription<TCall>[] subscriptions, TCall call, EventingOptions options,
        Task?[]? startedHandlerTasks, CancellationToken cancellationToken)
    {
        for (var index = 0; index < subscriptions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InvokeSubscriptionAsync(subscriptions[index], index, call, options, startedHandlerTasks).ConfigureAwait(false);
        }
    }

    private async Task RunInParallelAsync(EventSubscription<TCall>[] subscriptions, TCall call, EventingOptions options,
        Task?[]? startedHandlerTasks, CancellationToken cancellationToken)
    {
        var runningInvocations = new List<Task>(subscriptions.Length);
        var everyHandlerFitsTheLimit = options.MaxConcurrentHandlers >= subscriptions.Length;
        if (everyHandlerFitsTheLimit)
        {
            for (var index = 0; index < subscriptions.Length; index++)
            {
                runningInvocations.Add(InvokeSubscriptionAsync(subscriptions[index], index, call, options, startedHandlerTasks));
            }
            await Task.WhenAll(runningInvocations).ConfigureAwait(false);
            return;
        }

        //WaitAsync never blocks a thread, which keeps this usable on single-threaded Blazor WebAssembly
        using var concurrencyLimit = new SemaphoreSlim(options.MaxConcurrentHandlers);
        try
        {
            for (var index = 0; index < subscriptions.Length; index++)
            {
                await concurrencyLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
                runningInvocations.Add(InvokeThenReleaseAsync(subscriptions[index], index, call, options, startedHandlerTasks, concurrencyLimit));
            }
        }
        finally
        {
            //invocations never throw, and the semaphore must outlive every Release
            await Task.WhenAll(runningInvocations).ConfigureAwait(false);
        }
    }

    private async Task InvokeThenReleaseAsync(EventSubscription<TCall> subscription, int index, TCall call, EventingOptions options,
        Task?[]? startedHandlerTasks, SemaphoreSlim concurrencyLimit)
    {
        try
        {
            await InvokeSubscriptionAsync(subscription, index, call, options, startedHandlerTasks).ConfigureAwait(false);
        }
        finally
        {
            concurrencyLimit.Release();
        }
    }

    //never throws: handler failures are routed, slow handlers are handed to the background tracker, and a clock that fails
    //while timing a handler is reported while the handler is still observed
    private async Task InvokeSubscriptionAsync(EventSubscription<TCall> subscription, int index, TCall call, EventingOptions options,
        Task?[]? startedHandlerTasks)
    {
        //a subscription removed after this raise took its snapshot is skipped if its handler has not started yet
        if (!subscription.IsActive)
        {
            return;
        }

        var timeout = options.StartNextEventHandlerAfter;
        //only a handler that can be moved to the background is ever waited for by DisposeAsync, so only it needs its own scope
        var handlerScope = timeout is null ? null : EventCallbackScope.CreateNested();
        var handlerTask = StartHandler(subscription, call, handlerScope);
        if (startedHandlerTasks is not null)
        {
            startedHandlerTasks[index] = handlerTask;
        }

        var waitForHandlerToFinish = timeout is null || handlerTask.IsCompleted;
        if (waitForHandlerToFinish)
        {
            await ObserveHandlerAsync(handlerTask, subscription, call, options).ConfigureAwait(false);
            return;
        }

        using var timeoutCancellation = new CancellationTokenSource();
        var timeoutTask = TryStartHandlerTimeout(timeout.GetValueOrDefault(), subscription, call, options, timeoutCancellation.Token);
        if (timeoutTask is null)
        {
            //the clock could not time this handler (the failure is already routed), so it is waited for without a timeout,
            //which keeps it observed and lets the raise go on to the next handler afterwards
            await ObserveHandlerAsync(handlerTask, subscription, call, options).ConfigureAwait(false);
            return;
        }
        var firstFinished = await Task.WhenAny(handlerTask, timeoutTask).ConfigureAwait(false);
        if (firstFinished == handlerTask)
        {
            StopHandlerTimeout(timeoutCancellation, subscription, call, options);
            await ObserveHandlerAsync(handlerTask, subscription, call, options).ConfigureAwait(false);
            return;
        }

        //slow handler: stop waiting so the next handler can start, but keep observing it so its exceptions are still routed
        _backgroundWork.Track(ObserveHandlerAsync(handlerTask, subscription, call, options), handlerScope);
    }

    //the delay that moves a slow handler to the background; null when the clock threw while creating its timer (a custom
    //TimeProvider), which is reported as EventFailureStage.Dispatch instead of failing the raise after a handler already started
    private Task? TryStartHandlerTimeout(TimeSpan timeout, EventSubscription<TCall> subscription, TCall call, EventingOptions options,
        CancellationToken timeoutCancellationToken)
    {
        try
        {
            return Task.Delay(timeout + TimeoutGrace, options.TimeProvider, timeoutCancellationToken);
        }
        catch (Exception clockFailure)
        {
            RouteDispatchFailure(clockFailure, subscription, call, options);
            return null;
        }
    }

    //stops the delay's timer once the handler finished first. The delay's own cleanup is the token's only callback, so cancelling
    //inline is cheap; CancelAsync would queue that cleanup to the thread pool and resume this dispatch there, one extra hop per
    //handler. That cleanup disposes the clock's timer, so a custom TimeProvider whose timer throws on Dispose is reported here
    private void StopHandlerTimeout(CancellationTokenSource timeoutCancellation, EventSubscription<TCall> subscription, TCall call,
        EventingOptions options)
    {
        try
        {
            timeoutCancellation.Cancel();
        }
        catch (AggregateException callbackFailures)
        {
            //Cancel gathers the failing callbacks into one AggregateException; report each failure itself, as disposal does
            foreach (var clockFailure in callbackFailures.Flatten().InnerExceptions)
            {
                RouteDispatchFailure(clockFailure, subscription, call, options);
            }
        }
        catch (Exception clockFailure)
        {
            RouteDispatchFailure(clockFailure, subscription, call, options);
        }
    }

    private void RouteDispatchFailure(Exception exception, EventSubscription<TCall> subscription, TCall call, EventingOptions options)
    {
        var failure = new EventHandlerFailure(exception, EventFailureStage.Dispatch, Name, subscription.Id, call);
        RouteUnobservedFailure(failure, options);
    }

    private async Task ObserveHandlerAsync(Task handlerTask, EventSubscription<TCall> subscription, TCall call, EventingOptions options)
    {
        try
        {
            await handlerTask.ConfigureAwait(false);
        }
        catch (Exception firstException)
        {
            if (!handlerTask.IsFaulted)
            {
                //a cancelled handler task carries only the cancellation that await threw
                RouteHandlerException(firstException, subscription, call, options);
                return;
            }
            //await rethrows only the first exception, but a combined handler (a + b) or a handler returning Task.WhenAll
            //can fail in several places at once; route every one
            foreach (var exception in handlerTask.Exception!.InnerExceptions)
            {
                RouteHandlerException(exception, subscription, call, options);
            }
        }
    }
}
