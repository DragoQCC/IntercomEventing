namespace IntercomEventing;

/// <summary>
/// Raises on a fixed interval after <see cref="Start(TPayload, object?)"/>: once, or on every tick when <see cref="AutoReset"/> is set.
/// The payload given to <c>Start</c> is delivered with every tick of that run.
/// </summary>
/// <remarks>
/// <para>One schedule runs at a time. <c>Start</c> starts the schedule, or replaces the running one (and its payload);
/// <see cref="Stop"/> ends it, and a later <c>Start</c> begins a new one. For one timer per item (a session per user, for
/// example) create one event per item.</para>
/// <para>The dispatches of one schedule never overlap: the next tick waits until the current tick's dispatch finished or its
/// slow handlers moved to the background. A handler moved to the background can still be running when the next tick starts,
/// and after a replacing <c>Start</c> the old schedule's last tick may still be delivering while the new schedule runs.</para>
/// <para><see cref="EventingOptions.TimeProvider"/> drives the timer, so tests can advance it with a fake clock. A failure of
/// the schedule outside any handler is routed to <see cref="EventingOptions.DefaultExceptionHandler"/> as
/// <see cref="EventFailureStage.BackgroundRaise"/> and stops the schedule.</para>
/// <para>A custom <see cref="TimeProvider"/> whose <see cref="TimeProvider.CreateTimer"/> throws is not supported: the
/// failure is routed as above, but the runtime's timer cannot clean up after it and the process terminates when the garbage
/// collector later finalizes that timer.</para>
/// <para>Ticks do not run in the <see cref="ExecutionContext"/> of the code that called <c>Start</c>: its
/// <see cref="AsyncLocal{T}"/> values, such as <c>Activity.Current</c> or a logging scope of the request that started the
/// schedule, do not reach tick handlers.</para>
/// </remarks>
/// <typeparam name="TPayload">Data delivered with each tick, for example the user whose session expires.</typeparam>
/// <example>
/// <code>
/// var sessionExpired = new TimedEvent&lt;User&gt;(TimeSpan.FromMinutes(20), autoReset: false);
/// sessionExpired.Subscribe(call => Console.WriteLine($"{call.Payload.Name} timed out after {call.Interval}"));
/// sessionExpired.Start(user);
/// </code>
/// </example>
public class TimedEvent<TPayload> : IntercomEventBase<TimedEventCall<TPayload>>
{
    //PeriodicTimer accepts periods from 1 ms up to uint.MaxValue - 1 milliseconds (about 49.7 days); it would reject anything
    //outside that range only when Start runs the schedule in the background, so the constructor checks it up front
    private static readonly TimeSpan ShortestInterval = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan LongestInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly Lock _scheduleLock = new();

    //cancels the running schedule; null while stopped. The loop that owns the source disposes it when the loop exits
    private CancellationTokenSource? _runningScheduleCancellation;

    /// <summary>
    /// Time between ticks.
    /// </summary>
    public TimeSpan Interval { get; }

    /// <summary>
    /// Whether the timer keeps raising after the first tick.
    /// </summary>
    public bool AutoReset { get; }

    /// <summary>
    /// <see langword="true"/> from <c>Start</c> until <see cref="Stop"/>, disposal, the end of a one-shot run
    /// (<see cref="AutoReset"/> is <see langword="false"/>), or a background failure that stops the schedule.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_scheduleLock)
            {
                return _runningScheduleCancellation is not null;
            }
        }
    }

    /// <summary>
    /// Creates a stopped timer.
    /// </summary>
    /// <param name="interval">Time between ticks.</param>
    /// <param name="autoReset"><see langword="true"/> to raise on every tick until stopped; <see langword="false"/> to raise once per <c>Start</c>.</param>
    /// <param name="options">Dispatch settings; <see cref="EventingOptions.TimeProvider"/> drives the timer.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is shorter than 1 millisecond or longer than
    /// about 49.7 days.</exception>
    /// <example>
    /// <code>
    /// var sessionExpired = new TimedEvent&lt;SessionUser&gt;(TimeSpan.FromMinutes(20), autoReset: false);
    /// </code>
    /// </example>
    public TimedEvent(TimeSpan interval, bool autoReset = true, EventingOptions? options = null)
        : base(options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, ShortestInterval);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(interval, LongestInterval);
        Interval = interval;
        AutoReset = autoReset;
    }

    /// <summary>
    /// Starts the schedule, or replaces the running one. Returns immediately without raising: ticks are raised in the
    /// background, the first one <see cref="Interval"/> from now.
    /// </summary>
    /// <param name="payload">Delivered with every tick of this run.</param>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/>.</param>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <example>
    /// <code>
    /// sessionExpired.Start(new SessionUser("Ada"), caller: this);
    /// </code>
    /// </example>
    public void Start(TPayload payload, object? caller = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        CancellationTokenSource scheduleCancellation;
        lock (_scheduleLock)
        {
            CancelRunningSchedule();
            //linked to disposal, so disposing the event also ends this schedule
            scheduleCancellation = CancellationTokenSource.CreateLinkedTokenSource(DisposalToken);
            _runningScheduleCancellation = scheduleCancellation;
        }
        //started after the lock is released, so creating the timer (TimeProvider code) cannot hold up Stop, IsRunning or another
        //Start. A Start or Stop that takes the lock in between has already cancelled this source, so this loop then ends at its
        //first wait; only the loop that owns the source disposes it, so the source is still valid here.
        //Ticks arrive long after Start returned, so the schedule must not keep the ExecutionContext of whoever called Start: its
        //AsyncLocal values (Activity.Current, logging scopes, a tenant, this library's own handler scope when Start is called
        //from a handler) would otherwise reach every tick handler for the life of the schedule. The loop awaits the timer before
        //its first raise, as StartWithoutCallerContext requires
        _ = StartWithoutCallerContext(() => RunScheduleAsync(payload, caller, scheduleCancellation));
    }

    /// <summary>
    /// Stops the schedule. Never waits, so it is safe to call from inside a handler of this event: the tick being delivered
    /// still reaches every handler, and no further tick is raised. Handlers that are running keep running; their
    /// <see cref="EventCall.CancellationToken"/> is cancelled only by disposal. Does nothing when already stopped.
    /// </summary>
    /// <example>
    /// <code>
    /// heartbeat.Subscribe(call =>
    /// {
    ///     if (call.TickNumber == 10)
    ///     {
    ///         heartbeat.Stop();
    ///     }
    /// });
    /// </code>
    /// </example>
    public void Stop()
    {
        lock (_scheduleLock)
        {
            CancelRunningSchedule();
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposingAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    //caller holds _scheduleLock
    private void CancelRunningSchedule()
    {
        var scheduleToCancel = _runningScheduleCancellation;
        //cleared first: cancelling can run the loop's exit code inline, and that code must already see the schedule as replaced
        _runningScheduleCancellation = null;
        scheduleToCancel?.Cancel();
    }

    private async Task RunScheduleAsync(TPayload payload, object? caller, CancellationTokenSource scheduleCancellation)
    {
        var scheduleToken = scheduleCancellation.Token;
        try
        {
            //created before the first await, so the first tick is due one Interval after Start, also on a fake clock
            using var timer = new PeriodicTimer(Interval, Options.TimeProvider);
            long tickNumber = 0;
            //this first wait still runs on Start's thread. It completes synchronously when the tick is already due (Start's
            //thread was preempted or paused for an Interval or longer), and a plain await would then raise that tick inside
            //Start. ForceYielding moves the loop to the thread pool in that case and changes nothing otherwise: the loop
            //resumes where the tick completes, like every later wait. ValueTask has no ConfigureAwaitOptions overload, hence
            //AsTask for this one wait per Start; no Task.Run, which keeps the loop valid on single-threaded WebAssembly
            var tickArrived = await timer.WaitForNextTickAsync(scheduleToken).AsTask().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            while (tickArrived)
            {
                tickNumber++;
                var tickState = (Interval: Interval, TickNumber: tickNumber, Payload: payload);
                //not the schedule token: Stop() called from one of this tick's handlers ends the loop but must not
                //cancel delivery of the current tick to the remaining handlers (disposal still cancels it)
                await DispatchAsync(
                    tickState,
                    static (state, metadata, callCancellationToken) => new TimedEventCall<TPayload>(
                        state.Interval,
                        state.TickNumber,
                        state.Payload,
                        metadata,
                        callCancellationToken),
                    caller,
                    CancellationToken.None).ConfigureAwait(false);
                if (!AutoReset)
                {
                    break;
                }
                //the loop no longer runs inside Start, so a wait that completes synchronously just keeps it where it is
                tickArrived = await timer.WaitForNextTickAsync(scheduleToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (scheduleToken.IsCancellationRequested)
        {
            //stopped, replaced or disposed
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
            //the event was disposed between ticks
        }
        catch (Exception exception)
        {
            //nobody awaits the schedule, so an unexpected failure is routed instead of becoming an unobserved task exception
            RouteBackgroundRaiseFailure(exception);
        }
        finally
        {
            //forget the source before disposing it so a later Stop() cannot cancel a disposed source
            lock (_scheduleLock)
            {
                if (ReferenceEquals(_runningScheduleCancellation, scheduleCancellation))
                {
                    _runningScheduleCancellation = null;
                }
            }
            scheduleCancellation.Dispose();
        }
    }
}

/// <summary>
/// A <see cref="TimedEvent{TPayload}"/> without a payload, for heartbeats and periodic jobs.
/// </summary>
/// <example>
/// <code>
/// var heartbeat = new TimedEvent(TimeSpan.FromSeconds(30));
/// heartbeat.Subscribe(call => Console.WriteLine($"tick {call.TickNumber}"));
/// heartbeat.Start();
/// </code>
/// </example>
public class TimedEvent : TimedEvent<NoPayload>
{
    /// <summary>
    /// Creates a stopped timer.
    /// </summary>
    /// <param name="interval">Time between ticks.</param>
    /// <param name="autoReset"><see langword="true"/> to raise on every tick until stopped; <see langword="false"/> to raise once per <c>Start</c>.</param>
    /// <param name="options">Dispatch settings; <see cref="EventingOptions.TimeProvider"/> drives the timer.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is shorter than 1 millisecond or longer than
    /// about 49.7 days.</exception>
    /// <example>
    /// <code>
    /// var heartbeat = new TimedEvent(TimeSpan.FromSeconds(30));
    /// </code>
    /// </example>
    public TimedEvent(TimeSpan interval, bool autoReset = true, EventingOptions? options = null)
        : base(interval, autoReset, options)
    {
    }

    /// <summary>
    /// Starts the schedule, or replaces the running one.
    /// </summary>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/>.</param>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <example>
    /// <code>
    /// heartbeat.Start(caller: this);
    /// </code>
    /// </example>
    public void Start(object? caller = null)
    {
        Start(NoPayload.Value, caller);
    }
}
