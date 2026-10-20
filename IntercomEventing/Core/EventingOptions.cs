namespace IntercomEventing;

/// <summary>
/// Controls whether the handlers of one raise run one after another or at the same time.
/// </summary>
public enum DispatchMode
{
    /// <summary>
    /// Handlers start concurrently, limited by <see cref="EventingOptions.MaxConcurrentHandlers"/>. Each handler runs
    /// synchronously until its first <c>await</c>. While the handlers fit the limit, every one starts inline on the raising
    /// thread. A handler that has to wait for a free slot starts later without the raiser's <see cref="SynchronizationContext"/>,
    /// typically on a thread-pool thread.
    /// </summary>
    Parallel,

    /// <summary>
    /// Handlers run in subscription order; the next starts when the previous finishes
    /// or exceeds <see cref="EventingOptions.StartNextEventHandlerAfter"/>. Handlers start inline on the raising thread until
    /// one of them does not finish synchronously; the handlers after it start without the raiser's
    /// <see cref="SynchronizationContext"/>, typically on a thread-pool thread.
    /// </summary>
    /// <remarks>
    /// Sequential order does not keep a raise made from inside a handler behind the raise that triggered it: the dispatcher
    /// waits for that handler, nested raise included, so the nested call reaches the later subscribers first.
    /// </remarks>
    Sequential,
}

/// <summary>
/// Dispatch settings for an event. Pass an instance to an event's constructor, or set <see cref="Default"/>
/// (directly or through <c>AddIntercomEventing</c>) for every event created without one.
/// </summary>
/// <remarks>
/// <para>An event created without options reads <see cref="Default"/> each time it is raised, so changing the default
/// after events exist still applies to them.</para>
/// <para>Options passed to an event replace the defaults wholesale. To change one setting, derive from the default:
/// <c>EventingOptions.Default with { DispatchMode = DispatchMode.Sequential }</c>.</para>
/// <para>Every value is validated when the options are built, so invalid settings fail at construction rather than at a raise.</para>
/// </remarks>
/// <example>
/// <code>
/// var auditTrail = new IntercomEvent&lt;AuditEntry&gt;(new EventingOptions
/// {
///     DispatchMode = DispatchMode.Sequential,
///     StartNextEventHandlerAfter = TimeSpan.FromMilliseconds(250),
///     DefaultExceptionHandler = failure => logger.LogError(failure.Exception, "Handler failed for {Event}", failure.EventName),
/// });
/// </code>
/// </example>
public sealed record EventingOptions
{
    //Task.Delay with a TimeProvider accepts at most uint.MaxValue - 1 ms (about 49.7 days); the dispatcher adds a 50 ms grace
    private static readonly TimeSpan LongestHandlerTimeout = TimeSpan.FromDays(49);

    /// <summary>
    /// Options used by events constructed without their own <see cref="EventingOptions"/>. Read at every raise.
    /// </summary>
    /// <remarks>
    /// This is process-wide. Applications usually set it once at startup through <c>AddIntercomEventing</c>, which also
    /// rejects a second, conflicting configuration.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// EventingOptions.Default = EventingOptions.Default with { MaxConcurrentHandlers = 16 };
    /// </code>
    /// </example>
    public static EventingOptions Default
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = new();

    /// <summary>
    /// Whether handlers of one raise run sequentially or in parallel. Default: <see cref="DispatchMode.Parallel"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="IntercomEventing.DispatchMode"/>
    /// value, for example a cast from a number.</exception>
    public DispatchMode DispatchMode
    {
        get;
        init
        {
            //an undefined value (a bad cast, a numeric configuration value) would otherwise dispatch as Parallel without a word
            var isDefinedMode = Enum.IsDefined(value);
            if (!isDefinedMode)
            {
                throw new ArgumentOutOfRangeException(nameof(DispatchMode), value, "The value is not a defined DispatchMode.");
            }
            field = value;
        }
    } = DispatchMode.Parallel;

    /// <summary>
    /// The most handlers of one raise that may run at the same time in <see cref="DispatchMode.Parallel"/> mode; ignored in
    /// <see cref="DispatchMode.Sequential"/> mode. Default: 100. A handler that exceeded <see cref="StartNextEventHandlerAfter"/>
    /// no longer counts against this limit.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxConcurrentHandlers
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(MaxConcurrentHandlers));
            field = value;
        }
    } = 100;

    /// <summary>
    /// How long a handler may run before the dispatcher stops waiting for it and starts the next one. The slow handler keeps
    /// running in the background; its exceptions are still routed and <see cref="IntercomEventBase{TCall}.DisposeAsync"/>
    /// waits for it. Default: one second. <see langword="null"/> or <see cref="Timeout.InfiniteTimeSpan"/> waits for every handler.
    /// </summary>
    /// <remarks>
    /// <para>The dispatcher waits this long plus a 50 ms grace, so a handler that takes exactly the configured time is not
    /// moved to the background.</para>
    /// <para>The timeout bounds only the asynchronous part of a handler: synchronous work before its first <c>await</c> runs on
    /// the thread that starts the handler and is not interrupted.</para>
    /// </remarks>
    public TimeSpan? StartNextEventHandlerAfter
    {
        get;
        init
        {
            //both spellings of "no timeout" are stored as null so the dispatcher has a single case
            var waitForEveryHandler = value is null || value == Timeout.InfiniteTimeSpan;
            if (waitForEveryHandler)
            {
                field = null;
                return;
            }
            var timeout = value.GetValueOrDefault();
            field = timeout < TimeSpan.Zero ? TimeSpan.FromSeconds(1) : timeout > LongestHandlerTimeout ? LongestHandlerTimeout : timeout;
        }
    } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// When <see langword="false"/>, subscribing to an event that already has a subscriber throws
    /// <see cref="InvalidOperationException"/>. Default: true.
    /// </summary>
    public bool AllowMultipleSubscribers { get; init; } = true;

    /// <summary>
    /// Receives exceptions from handlers whose subscription has no exception handler of its own, from subscription hooks
    /// that nobody awaited, from the background work of timed and debounce events, and from disposal.
    /// <see cref="EventHandlerFailure.Stage"/> says which. When no handler exists, the exception is swallowed so one failing
    /// handler cannot break the raise. An exception thrown by this handler itself is contained and never reaches the raiser.
    /// </summary>
    /// <remarks>
    /// A handler that ends with <see cref="OperationCanceledException"/> after its <see cref="EventCall.CancellationToken"/>
    /// was cancelled counts as cancelled and is not reported here.
    /// </remarks>
    public Action<EventHandlerFailure>? DefaultExceptionHandler { get; init; }

    /// <summary>
    /// Clock used for timestamps, handler timeouts, timed events and debounce intervals. Replace it in tests
    /// (for example with <c>FakeTimeProvider</c>). Default: <see cref="TimeProvider.System"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public TimeProvider TimeProvider
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(TimeProvider));
            field = value;
        }
    } = TimeProvider.System;
}
