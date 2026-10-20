using System.Diagnostics;

namespace IntercomEventing.Tests.TestSupport;

/// <summary>
/// Waits for work that other threads finish. Only a failing test ever waits a full timeout, so the timeouts are generous
/// enough to stay reliable on a loaded machine without slowing down a passing run.
/// </summary>
internal static class Eventually
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// How long a condition or task may take before the test treats it as hung.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a test watches something it expects to keep waiting. A defect that makes it finish early (a missing wait,
    /// a skipped handler) finishes within microseconds, so this window detects it reliably.
    /// </summary>
    public static readonly TimeSpan StillWaitingWindow = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Polls <paramref name="condition"/> until it is true or the timeout passes.
    /// </summary>
    /// <returns><see langword="true"/> once the condition held; <see langword="false"/> if it never did.</returns>
    public static async Task<bool> IsTrueAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < limit)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(PollInterval);
        }
        return condition();
    }

    /// <summary>
    /// Waits until <paramref name="task"/> finishes (in any state) or the timeout passes.
    /// </summary>
    /// <returns><see langword="true"/> if the task finished in time.</returns>
    public static async Task<bool> CompletesAsync(Task task, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        //SuppressThrowing: only completion matters here, the caller inspects the task's outcome itself
        await task.WaitAsync(limit).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return task.IsCompleted;
    }

    /// <summary>
    /// Watches <paramref name="task"/> for <see cref="StillWaitingWindow"/> and reports whether it was still running at the end.
    /// </summary>
    /// <returns><see langword="true"/> if the task did not finish during the window.</returns>
    public static async Task<bool> StaysIncompleteAsync(Task task)
    {
        await task.WaitAsync(StillWaitingWindow).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return !task.IsCompleted;
    }
}
