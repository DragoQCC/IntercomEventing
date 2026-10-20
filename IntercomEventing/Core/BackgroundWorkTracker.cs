namespace IntercomEventing;

/// <summary>
/// Keeps track of work an event started but does not await (slow handlers past their timeout, unsubscribe hooks started by
/// <c>-=</c> or <c>Dispose()</c>, subscribe hooks whose caller stopped waiting) so event disposal can wait for it. Tracked
/// tasks must not throw.
/// </summary>
internal sealed class BackgroundWorkTracker
{
    private readonly Lock _runningLock = new();

    //each running task with the scope of the user code it observes, so DisposeAsync called from inside that code can skip it
    private readonly Dictionary<Task, EventCallbackScope?> _runningWork = [];

    /// <summary>
    /// Number of tracked tasks that have not finished yet.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_runningLock)
            {
                return _runningWork.Count;
            }
        }
    }

    /// <summary>
    /// Tracks <paramref name="work"/> until it finishes.
    /// </summary>
    /// <param name="work">A task that never faults.</param>
    /// <param name="scope">The scope of the user code <paramref name="work"/> waits for, or <see langword="null"/>.</param>
    public void Track(Task work, EventCallbackScope? scope)
    {
        lock (_runningLock)
        {
            _runningWork[work] = scope;
        }
        //added before this continuation is attached, so a task that already finished is still removed
        _ = RemoveWhenFinishedAsync(work);
    }

    /// <summary>
    /// Waits until every tracked task has finished, including tasks tracked while waiting, except the work that
    /// <paramref name="callingScope"/> belongs to: that work is waiting for the caller and would never finish first.
    /// </summary>
    /// <param name="callingScope">The scope of the code that is waiting, from <see cref="EventCallbackScope.Current"/>.</param>
    public async Task WhenAllAsync(EventCallbackScope? callingScope)
    {
        //loop because work can join while disposal waits, even though every subscription was already removed: a handler of a
        //raise that was still delivering inline crosses StartNextEventHandlerAfter and moves to the background, or the caller
        //of a SubscribeAsync stops waiting for its OnSubscribed hook, which then finishes in the background
        while (true)
        {
            var workToWaitFor = SnapshotWorkToWaitFor(callingScope);
            if (workToWaitFor.Count == 0)
            {
                return;
            }
            await Task.WhenAll(workToWaitFor).ConfigureAwait(false);
        }
    }

    private List<Task> SnapshotWorkToWaitFor(EventCallbackScope? callingScope)
    {
        var workToWaitFor = new List<Task>();
        lock (_runningLock)
        {
            foreach (var (work, workScope) in _runningWork)
            {
                var workIsTheCallersOwn = workScope is not null && workScope.IsSameAsOrEncloses(callingScope);
                if (!workIsTheCallersOwn)
                {
                    workToWaitFor.Add(work);
                }
            }
        }
        return workToWaitFor;
    }

    private async Task RemoveWhenFinishedAsync(Task work)
    {
        await work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (_runningLock)
        {
            _runningWork.Remove(work);
        }
    }
}
