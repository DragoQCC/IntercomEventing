namespace IntercomEventing;

/// <summary>
/// Coalesces bursts of triggers into one raise: the event raises once <see cref="DebounceInterval"/> has passed without a
/// new <see cref="Trigger(TPayload, object?)"/>, carrying the payload (and caller) of the last trigger.
/// </summary>
/// <remarks>
/// <para><see cref="Trigger(TPayload, object?)"/> never waits; the raise happens later in the background. Call
/// <see cref="FlushAsync"/> to raise a pending trigger immediately instead of after its quiet period. It does not wait for a
/// raise that the background already started once the quiet period ended.</para>
/// <para>Disposing the event drops a pending raise. Each raise reports how many triggers it absorbed in
/// <see cref="DebounceEventCall{TPayload}.CoalescedTriggerCount"/>.</para>
/// <para>A trigger that arrives while the previous burst's raise is still delivering starts a new burst, so two raises can
/// deliver concurrently; raises are not serialized. A failure of the background wait outside any handler is routed to
/// <see cref="EventingOptions.DefaultExceptionHandler"/> as <see cref="EventFailureStage.BackgroundRaise"/>.</para>
/// <para>The background raise runs in the <see cref="ExecutionContext"/> of the last <c>Trigger</c> call of the burst, like work
/// that call started: its <see cref="AsyncLocal{T}"/> values, such as <c>Activity.Current</c>, reach the handlers. Trigger
/// inside <c>using (ExecutionContext.SuppressFlow())</c> when they must not.</para>
/// </remarks>
/// <typeparam name="TPayload">The data of each trigger; only the last one in a burst is delivered.</typeparam>
/// <example>
/// <code>
/// public DebounceEvent&lt;SearchQuery&gt; SearchChanged { get; } = new(TimeSpan.FromMilliseconds(300));
///
/// // called on every keystroke; handlers run once the user pauses
/// SearchChanged.Trigger(new SearchQuery(textBox.Text));
///
/// searchBox.SearchChanged.Subscribe(async call => await RunSearchAsync(call.Payload.Text, call.CancellationToken));
/// </code>
/// </example>
public class DebounceEvent<TPayload> : IntercomEventBase<DebounceEventCall<TPayload>>
{
    //Task.Delay with a TimeProvider accepts at most uint.MaxValue - 1 milliseconds (about 49.7 days)
    private static readonly TimeSpan LongestDebounceInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly Lock _pendingLock = new();

    //the latest trigger, waiting for its quiet period; replacing it supersedes the previous trigger, null when nothing is pending
    private PendingTrigger? _pendingTrigger;
    private int _triggersSinceLastRaise;

    /// <summary>
    /// How long triggers must stop before the event raises.
    /// </summary>
    public TimeSpan DebounceInterval { get; }

    /// <summary>
    /// <see langword="true"/> while a trigger is waiting for its quiet period to pass.
    /// </summary>
    public bool HasPendingRaise
    {
        get
        {
            lock (_pendingLock)
            {
                return _pendingTrigger is not null;
            }
        }
    }

    /// <summary>
    /// Creates the event.
    /// </summary>
    /// <param name="debounceInterval">How long triggers must stop before the event raises.</param>
    /// <param name="options">Dispatch settings; <see cref="EventingOptions.TimeProvider"/> drives the interval.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="debounceInterval"/> is zero, negative or longer than about 49.7 days.</exception>
    /// <example>
    /// <code>
    /// var searchChanged = new DebounceEvent&lt;SearchQuery&gt;(TimeSpan.FromMilliseconds(300));
    /// </code>
    /// </example>
    public DebounceEvent(TimeSpan debounceInterval, EventingOptions? options = null)
        : base(options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(debounceInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(debounceInterval, LongestDebounceInterval);
        DebounceInterval = debounceInterval;
    }

    /// <summary>
    /// Records a trigger and restarts the quiet period. Returns immediately; the raise happens later in the background.
    /// </summary>
    /// <param name="payload">Delivered if this turns out to be the last trigger of the burst.</param>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/> (the last trigger's caller wins).</param>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <example>
    /// <code>
    /// SearchChanged.Trigger(new SearchQuery(textBox.Text), caller: this);
    /// </code>
    /// </example>
    public void Trigger(TPayload payload, object? caller = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        PendingTrigger latestTrigger;
        lock (_pendingLock)
        {
            //linked to disposal, so disposing the event cancels the pending raise
            var quietPeriod = CancellationTokenSource.CreateLinkedTokenSource(DisposalToken);
            latestTrigger = new PendingTrigger(payload, caller, quietPeriod);
            var supersededTrigger = _pendingTrigger;
            _pendingTrigger = latestTrigger;
            _triggersSinceLastRaise++;
            //replaced first: cancelling can run the superseded wait's exit code inline, and it must already see itself replaced
            supersededTrigger?.QuietPeriod.Cancel();
        }
        //the background wait belongs to no handler, even when Trigger is called from inside one
        using (EventCallbackScope.EnterDetached())
        {
            _ = RaiseAfterQuietPeriodAsync(latestTrigger);
        }
    }

    /// <summary>
    /// Raises the pending trigger now instead of waiting for its quiet period, and awaits that raise like
    /// <see cref="IntercomEvent{TPayload}.RaiseAsync(TPayload, object?, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// Only a trigger still waiting for its quiet period (<see cref="HasPendingRaise"/>) is raised. Once the quiet period has
    /// ended, the background raise owns the trigger: <c>FlushAsync</c> then returns <see langword="false"/> at once and does
    /// not wait for that raise, whose handlers may still be running.
    /// </remarks>
    /// <param name="cancellationToken">Stops starting further handlers; when already cancelled, the pending trigger stays pending.</param>
    /// <returns><see langword="true"/> if a pending trigger was raised; <see langword="false"/> if nothing was pending, including
    /// when the background raise had already started.</returns>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example>
    /// <code>
    /// // the user pressed Save: deliver the latest edit now instead of after the quiet period
    /// bool raisedPendingEdit = await DocumentEdited.FlushAsync();
    /// </code>
    /// </example>
    public async Task<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        PendingTrigger? flushedTrigger;
        int coalescedTriggerCount;
        lock (_pendingLock)
        {
            flushedTrigger = _pendingTrigger;
            if (flushedTrigger is null)
            {
                return false;
            }
            coalescedTriggerCount = TakePendingTriggerLocked();
            //ends the quiet-period wait, which then disposes its own source
            flushedTrigger.QuietPeriod.Cancel();
        }
        await RaiseTriggerAsync(flushedTrigger, coalescedTriggerCount, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposingAsync()
    {
        lock (_pendingLock)
        {
            //the quiet-period wait observes DisposalToken; the pending trigger is intentionally dropped
            _pendingTrigger = null;
            _triggersSinceLastRaise = 0;
        }
        return ValueTask.CompletedTask;
    }

    //caller holds _pendingLock and has checked that a trigger is pending; returns the number of triggers it absorbed
    private int TakePendingTriggerLocked()
    {
        var coalescedTriggerCount = _triggersSinceLastRaise;
        _pendingTrigger = null;
        _triggersSinceLastRaise = 0;
        return coalescedTriggerCount;
    }

    private Task RaiseTriggerAsync(PendingTrigger trigger, int coalescedTriggerCount, CancellationToken cancellationToken)
    {
        var raiseState = (DebounceInterval: DebounceInterval, CoalescedTriggerCount: coalescedTriggerCount, Payload: trigger.Payload);
        return DispatchAsync(
            raiseState,
            static (state, metadata, callCancellationToken) => new DebounceEventCall<TPayload>(
                state.DebounceInterval,
                state.CoalescedTriggerCount,
                state.Payload,
                metadata,
                callCancellationToken),
            trigger.Caller,
            cancellationToken);
    }

    private async Task RaiseAfterQuietPeriodAsync(PendingTrigger trigger)
    {
        try
        {
            //this await still runs on Trigger's thread. The delay has already completed when the quiet period is over by now
            //(the triggering thread was preempted or paused for a DebounceInterval or longer), and a plain await would then
            //raise inside Trigger. ForceYielding moves the raise to the thread pool in that case and changes nothing otherwise:
            //the raise runs where the delay completes, and no extra thread-pool hop is added
            await Task.Delay(DebounceInterval, Options.TimeProvider, trigger.QuietPeriod.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            int coalescedTriggerCount;
            lock (_pendingLock)
            {
                //a trigger, FlushAsync or disposal that arrived while this delay was completing takes over
                var isStillTheLatestTrigger = ReferenceEquals(_pendingTrigger, trigger);
                if (!isStillTheLatestTrigger)
                {
                    return;
                }
                coalescedTriggerCount = TakePendingTriggerLocked();
            }
            //not cancellable by later triggers: once the raise starts, a new burst begins its own quiet period
            await RaiseTriggerAsync(trigger, coalescedTriggerCount, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (trigger.QuietPeriod.IsCancellationRequested)
        {
            //superseded by a newer trigger, flushed, or the event was disposed
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
            //disposed between the quiet period and the raise
        }
        catch (Exception exception)
        {
            //nobody awaits this wait, so an unexpected failure is routed instead of becoming an unobserved task exception
            RouteBackgroundRaiseFailure(exception);
        }
        finally
        {
            //forget the trigger before disposing its source so a later Trigger() cannot cancel a disposed source
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pendingTrigger, trigger))
                {
                    _pendingTrigger = null;
                }
            }
            trigger.QuietPeriod.Dispose();
        }
    }

    //one trigger's data plus the source that ends its quiet period; its identity tells a finished wait whether it is still the latest
    private sealed class PendingTrigger
    {
        public TPayload Payload { get; }

        public object? Caller { get; }

        public CancellationTokenSource QuietPeriod { get; }

        public PendingTrigger(TPayload payload, object? caller, CancellationTokenSource quietPeriod)
        {
            Payload = payload;
            Caller = caller;
            QuietPeriod = quietPeriod;
        }
    }
}

/// <summary>
/// A <see cref="DebounceEvent{TPayload}"/> without a payload, for "something changed, recompute once things settle".
/// </summary>
/// <example>
/// <code>
/// var layoutChanged = new DebounceEvent(TimeSpan.FromMilliseconds(100));
/// layoutChanged.Subscribe(call => RecalculateLayout());
/// layoutChanged.Trigger();
/// </code>
/// </example>
public class DebounceEvent : DebounceEvent<NoPayload>
{
    /// <summary>
    /// Creates the event.
    /// </summary>
    /// <param name="debounceInterval">How long triggers must stop before the event raises.</param>
    /// <param name="options">Dispatch settings; <see cref="EventingOptions.TimeProvider"/> drives the interval.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="debounceInterval"/> is zero, negative or longer than about 49.7 days.</exception>
    /// <example>
    /// <code>
    /// var layoutChanged = new DebounceEvent(TimeSpan.FromMilliseconds(100));
    /// </code>
    /// </example>
    public DebounceEvent(TimeSpan debounceInterval, EventingOptions? options = null)
        : base(debounceInterval, options)
    {
    }

    /// <summary>
    /// Records a trigger and restarts the quiet period. Returns immediately.
    /// </summary>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/>.</param>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <example>
    /// <code>
    /// layoutChanged.Trigger(caller: this);
    /// </code>
    /// </example>
    public void Trigger(object? caller = null)
    {
        Trigger(NoPayload.Value, caller);
    }
}
