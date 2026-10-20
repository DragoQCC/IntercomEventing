namespace IntercomEventing.Tests.TestSupport;

/// <summary>
/// Runs a step on several dedicated threads, round after round, releasing every thread at the same moment at the start of
/// each round. The calls of one round really overlap, so a missing lock shows up even while the rest of the suite keeps the
/// thread pool busy; work items queued with <c>Task.Run</c> would often run one after another instead.
/// </summary>
/// <example>
/// <code>
/// await LockstepThreads.RunAsync(threadCount: 2, rounds: 1000, (threadIndex, round) =>
/// {
///     removals[round, threadIndex] = events[round].Unsubscribe(handler);
/// }).WaitAsync(Eventually.DefaultTimeout);
/// </code>
/// </example>
internal static class LockstepThreads
{
    /// <summary>
    /// Runs <paramref name="step"/> once per thread in every round.
    /// </summary>
    /// <param name="threadCount">Number of dedicated threads.</param>
    /// <param name="rounds">Number of rounds; each thread waits for the others before it starts a round.</param>
    /// <param name="step">Called with the thread index (0 to <paramref name="threadCount"/> - 1) and the round number.</param>
    /// <returns>A task that completes when every thread finished; it faults with a step's exception.</returns>
    public static async Task RunAsync(int threadCount, int rounds, Action<int, int> step)
    {
        var roundStart = new Barrier(threadCount);
        var threadsFinished = new Task[threadCount];
        for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            var thisThreadIndex = threadIndex;
            var threadFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            threadsFinished[threadIndex] = threadFinished.Task;
            var thread = new Thread(() => RunEveryRound(thisThreadIndex, rounds, roundStart, step, threadFinished))
            {
                IsBackground = true,
                Name = $"Lockstep thread {thisThreadIndex}",
            };
            thread.Start();
        }

        try
        {
            await Task.WhenAll(threadsFinished);
        }
        finally
        {
            //every thread has stopped using the barrier once all of them finished
            roundStart.Dispose();
        }
    }

    private static void RunEveryRound(int threadIndex, int rounds, Barrier roundStart, Action<int, int> step, TaskCompletionSource threadFinished)
    {
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                var allThreadsArrived = roundStart.SignalAndWait(Eventually.DefaultTimeout);
                if (!allThreadsArrived)
                {
                    throw new TimeoutException($"Lockstep thread {threadIndex} waited too long for the other threads in round {round}.");
                }
                step(threadIndex, round);
            }
            threadFinished.SetResult();
        }
        catch (Exception exception)
        {
            //an exception must not escape a dedicated thread (it would end the test process); the test sees it through the task
            LeaveBarrier(roundStart);
            threadFinished.SetException(exception);
        }
    }

    //lets the other threads finish their rounds instead of waiting for a thread that stopped
    private static void LeaveBarrier(Barrier roundStart)
    {
        try
        {
            roundStart.RemoveParticipant();
        }
        catch (InvalidOperationException)
        {
            //no participant left to remove; ObjectDisposedException derives from InvalidOperationException as well
        }
    }
}
