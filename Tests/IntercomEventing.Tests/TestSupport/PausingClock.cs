namespace IntercomEventing.Tests.TestSupport;

/// <summary>
/// The system clock, except that the next <see cref="CreateTimer"/> call (once armed) keeps the creating thread busy for
/// longer than the timer's due time. That models the thread being preempted, or paused by a garbage collection, between
/// creating a timer and awaiting it: the first wait of a timed or debounce event then finds its tick already due.
/// </summary>
/// <remarks>
/// If the machine is so loaded that the real timer still has not fired after the pause, the wait is simply pending and the
/// code under test takes its ordinary path, so a test using this clock can miss the edge case but never fails because of it.
/// </remarks>
internal sealed class PausingClock : TimeProvider
{
    private static readonly TimeSpan PauseBeyondTheDueTime = TimeSpan.FromMilliseconds(40);

    private volatile bool _pauseNextTimerCreation;

    /// <summary>
    /// Set to make the next <see cref="CreateTimer"/> call pause; it clears itself after one use.
    /// </summary>
    public bool PauseNextTimerCreation
    {
        get => _pauseNextTimerCreation;
        set => _pauseNextTimerCreation = value;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = System.CreateTimer(callback, state, dueTime, period);
        if (_pauseNextTimerCreation)
        {
            _pauseNextTimerCreation = false;
            Thread.Sleep(dueTime + PauseBeyondTheDueTime);
        }
        return timer;
    }
}
