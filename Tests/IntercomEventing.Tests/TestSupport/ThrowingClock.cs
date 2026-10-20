namespace IntercomEventing.Tests.TestSupport;

/// <summary>
/// A <see cref="TimeProvider"/> that forwards to another one (usually a <c>FakeTimeProvider</c>) and can be told to fail:
/// <see cref="GetUtcNow"/> throws while <see cref="ThrowOnGetUtcNow"/> is set, <see cref="CreateTimer"/> throws while
/// <see cref="ThrowOnCreateTimer"/> is set, and disposing a timer it created throws while <see cref="ThrowOnTimerDispose"/> is set.
/// </summary>
/// <remarks>
/// <para>Every raise reads <see cref="GetUtcNow"/> for <see cref="EventMetadata.RaisedAt"/>, so this is the simplest way to make a
/// raise fail outside any handler: a timed event's schedule or a debounce wait then has to route the failure itself.</para>
/// <para>The dispatcher creates a timer for each handler that does not finish synchronously (the
/// <see cref="EventingOptions.StartNextEventHandlerAfter"/> timeout) and disposes it when the handler finishes first, so the
/// two timer switches make the dispatcher's own timing fail.</para>
/// </remarks>
internal sealed class ThrowingClock : TimeProvider
{
    private readonly TimeProvider _innerClock;
    private volatile bool _throwOnGetUtcNow;
    private volatile bool _throwOnCreateTimer;
    private volatile bool _throwOnTimerDispose;

    /// <summary>
    /// While <see langword="true"/>, <see cref="GetUtcNow"/> throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool ThrowOnGetUtcNow
    {
        get => _throwOnGetUtcNow;
        set => _throwOnGetUtcNow = value;
    }

    /// <summary>
    /// While <see langword="true"/>, <see cref="CreateTimer"/> throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool ThrowOnCreateTimer
    {
        get => _throwOnCreateTimer;
        set => _throwOnCreateTimer = value;
    }

    /// <summary>
    /// While <see langword="true"/>, disposing a timer this clock created throws <see cref="InvalidOperationException"/>
    /// (after the inner timer was disposed).
    /// </summary>
    public bool ThrowOnTimerDispose
    {
        get => _throwOnTimerDispose;
        set => _throwOnTimerDispose = value;
    }

    public override long TimestampFrequency => _innerClock.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => _innerClock.LocalTimeZone;

    public ThrowingClock(TimeProvider innerClock)
    {
        _innerClock = innerClock;
    }

    public override DateTimeOffset GetUtcNow()
    {
        if (_throwOnGetUtcNow)
        {
            throw new InvalidOperationException("clock failure");
        }
        return _innerClock.GetUtcNow();
    }

    public override long GetTimestamp()
    {
        return _innerClock.GetTimestamp();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (_throwOnCreateTimer)
        {
            throw new InvalidOperationException("timer creation failed");
        }
        var innerTimer = _innerClock.CreateTimer(callback, state, dueTime, period);
        return new DisposeFailingTimer(innerTimer, this);
    }

    //forwards to the inner timer; Dispose throws after disposing it while the clock's ThrowOnTimerDispose is set
    private sealed class DisposeFailingTimer : ITimer
    {
        private readonly ITimer _innerTimer;
        private readonly ThrowingClock _clock;

        public DisposeFailingTimer(ITimer innerTimer, ThrowingClock clock)
        {
            _innerTimer = innerTimer;
            _clock = clock;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            return _innerTimer.Change(dueTime, period);
        }

        public void Dispose()
        {
            _innerTimer.Dispose();
            if (_clock.ThrowOnTimerDispose)
            {
                throw new InvalidOperationException("timer disposal failed");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _innerTimer.DisposeAsync();
            if (_clock.ThrowOnTimerDispose)
            {
                throw new InvalidOperationException("timer disposal failed");
            }
        }
    }
}
