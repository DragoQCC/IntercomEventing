namespace IntercomEventing.Samples.BlazorServer.EventKinds.Throttle;

/// <summary>
/// A custom event kind: raises at most once per <see cref="Interval"/>, however often <see cref="Offer"/> is called. Use it
/// to feed a fast stream (sensor readings, progress updates) to handlers that cannot keep up, such as a UI.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>The first offer after a quiet period raises at once.</description></item>
/// <item><description>Offers that arrive during the following <see cref="Interval"/> replace each other. When the interval
/// ends, the latest one is raised and a new interval starts, so the last value of a burst is never lost.</description></item>
/// <item><description>The interval starts when a raise has finished delivering, so raises never overlap.</description></item>
/// <item><description><see cref="Offer"/> never waits; raises happen in the background. A failure outside any handler goes to
/// <see cref="EventingOptions.DefaultExceptionHandler"/> as <see cref="EventFailureStage.BackgroundRaise"/>.</description></item>
/// </list>
/// <para>It is built only on the protected members that <see cref="IntercomEventBase{TCall}"/> gives every event kind:
/// <c>DispatchAsync</c>, <c>StartWithoutCallerContext</c>, <c>DisposalToken</c>, <c>OnDisposingAsync</c>,
/// <c>RouteBackgroundRaiseFailure</c> and <see cref="IntercomEventBase{TCall}.Options"/>. Subscribing, exception routing, timeouts and disposal come from the base.</para>
/// </remarks>
/// <typeparam name="TPayload">The data of each offer; only the raised offers reach the handlers.</typeparam>
/// <example>
/// <code>
/// var readingSampled = new ThrottleEvent&lt;SensorReading&gt;(TimeSpan.FromMilliseconds(500));
/// readingSampled.Subscribe(call => Console.WriteLine($"{call.Payload.Celsius} °C ({call.DroppedOfferCount} readings skipped)"));
///
/// // called for every reading, ten times a second; handlers run at most twice a second
/// readingSampled.Offer(reading, caller: this);
/// </code>
/// </example>
public sealed class ThrottleEvent<TPayload> : IntercomEventBase<ThrottleEventCall<TPayload>>
{
    private readonly Lock _windowLock = new();

    //true from the raise of an offer until Interval has passed without a newer one; offers are only stored meanwhile
    private bool _isWindowOpen;

    //the latest offer that arrived while the window was open; raised when the window ends
    private PendingOffer? _waitingOffer;

    //offers that a later offer replaced before they could be raised
    private int _droppedOfferCount;

    /// <summary>
    /// The minimum time between the end of one raise and the start of the next.
    /// </summary>
    public TimeSpan Interval { get; }

    /// <summary>
    /// Creates the event.
    /// </summary>
    /// <param name="interval">The minimum time between raises; must be positive.</param>
    /// <param name="options">Dispatch settings; <see langword="null"/> uses <see cref="EventingOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is zero or negative.</exception>
    public ThrottleEvent(TimeSpan interval, EventingOptions? options = null)
        : base(options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        Interval = interval;
    }

    /// <summary>
    /// Offers a payload: raises it at once when no raise happened during the last <see cref="Interval"/>, and otherwise keeps it
    /// until the interval ends, replacing an offer that is already waiting. Never waits.
    /// </summary>
    /// <param name="payload">The data to raise.</param>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/>.</param>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    public void Offer(TPayload payload, object? caller = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var offer = new PendingOffer(payload, caller);
        lock (_windowLock)
        {
            if (_isWindowOpen)
            {
                //an offer still waiting is replaced, so it will never be raised
                if (_waitingOffer is not null)
                {
                    _droppedOfferCount++;
                }
                _waitingOffer = offer;
                return;
            }
            _isWindowOpen = true;
        }
        //the loop outlives this call, and Offer is often called from inside another event's handler (here a TimedEvent
        //tick): without the caller's ExecutionContext, that handler's AsyncLocal values do not flow into every later raise
        _ = StartWithoutCallerContext(() => RunWindowLoopAsync(offer));
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposingAsync()
    {
        lock (_windowLock)
        {
            //DisposalToken is already cancelled, which ends the window loop; an offer still waiting is dropped
            _waitingOffer = null;
            _droppedOfferCount = 0;
        }
        return ValueTask.CompletedTask;
    }

    private async Task RunWindowLoopAsync(PendingOffer firstOffer)
    {
        try
        {
            //leaves Offer's thread (and any SynchronizationContext) before the first raise, so Offer never runs handlers inline;
            //StartWithoutCallerContext also needs the work to yield before it raises, so the handlers run in a clean context
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

            var offerToRaise = firstOffer;
            var droppedOfferCount = 0;
            while (true)
            {
                await RaiseOfferAsync(offerToRaise, droppedOfferCount).ConfigureAwait(false);
                //the window: offers that arrive during this delay are stored, and only the latest one is raised next
                await Task.Delay(Interval, Options.TimeProvider, DisposalToken).ConfigureAwait(false);
                lock (_windowLock)
                {
                    if (_waitingOffer is null)
                    {
                        //nothing arrived during the window, so the next offer raises at once
                        _isWindowOpen = false;
                        return;
                    }
                    offerToRaise = _waitingOffer;
                    droppedOfferCount = _droppedOfferCount;
                    _waitingOffer = null;
                    _droppedOfferCount = 0;
                }
            }
        }
        catch (OperationCanceledException) when (DisposalToken.IsCancellationRequested)
        {
            //the event was disposed during the window
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
            //the event was disposed just before a raise
        }
        catch (Exception exception)
        {
            //nobody awaits the loop, so an unexpected failure (a failing TimeProvider, for example) is reported instead of
            //becoming an unobserved task exception, and the window is closed so later offers raise again
            lock (_windowLock)
            {
                _isWindowOpen = false;
                _waitingOffer = null;
                _droppedOfferCount = 0;
            }
            RouteBackgroundRaiseFailure(exception);
        }
    }

    private Task RaiseOfferAsync(PendingOffer offer, int droppedOfferCount)
    {
        //the raise data goes into the call through the state argument; a static lambda allocates nothing without subscribers
        var raiseState = (Interval: Interval, DroppedOfferCount: droppedOfferCount, Payload: offer.Payload);
        //not cancellable by later offers; disposing the event still cancels the call's token for running handlers
        return DispatchAsync(
            raiseState,
            static (state, metadata, callCancellationToken) => new ThrottleEventCall<TPayload>(
                state.Interval,
                state.DroppedOfferCount,
                state.Payload,
                metadata,
                callCancellationToken),
            offer.Caller,
            CancellationToken.None);
    }

    private sealed record PendingOffer(TPayload Payload, object? Caller);
}
