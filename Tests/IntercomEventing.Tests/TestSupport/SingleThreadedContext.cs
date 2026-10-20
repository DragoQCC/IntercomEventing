using System.Collections.Concurrent;
using System.Diagnostics;

namespace IntercomEventing.Tests.TestSupport;

/// <summary>
/// Runs async code on one dedicated thread whose <see cref="SynchronizationContext"/> is a message pump, the shape of a UI
/// thread or the single Blazor WebAssembly thread. Continuations that capture the context queue up on that thread, so any
/// library code that blocks the thread while waiting for such a continuation deadlocks, and the run reports a timeout.
/// </summary>
/// <example>
/// <code>
/// var completed = await SingleThreadedContext.RunAsync(async () =>
/// {
///     subscription.Dispose();
///     await evt.DisposeAsync();
/// }, TimeSpan.FromSeconds(10));
/// </code>
/// </example>
internal sealed class SingleThreadedContext : SynchronizationContext
{
    //the pump reports its own outcome first; the caller's side gives up only this much later, when the pump thread is stuck
    private static readonly TimeSpan BlockedPumpAllowance = TimeSpan.FromSeconds(1);

    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _pendingCallbacks = new();

    private SingleThreadedContext()
    {
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a new pump thread until it finishes or <paramref name="timeout"/> passes.
    /// </summary>
    /// <returns><see langword="true"/> if the body finished in time; a body that throws faults the returned task.</returns>
    public static async Task<bool> RunAsync(Func<Task> body, TimeSpan timeout)
    {
        var runOutcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumpThread = new Thread(() => PumpUntilFinished(body, timeout, runOutcome))
        {
            IsBackground = true,
            Name = "SingleThreadedContext pump",
        };
        pumpThread.Start();
        try
        {
            //the timeout is also enforced from here: code that blocks the pump thread synchronously (the S7 defect) never
            //returns to the pump loop, so the loop's own timeout check never runs. The stuck background thread is abandoned
            return await runOutcome.Task.WaitAsync(timeout + BlockedPumpAllowance);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public override void Post(SendOrPostCallback callback, object? state)
    {
        try
        {
            _pendingCallbacks.Add((callback, state));
        }
        catch (InvalidOperationException)
        {
            //the pump already stopped (the body finished or timed out); run late continuations elsewhere rather than lose them
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    public override void Send(SendOrPostCallback callback, object? state)
    {
        throw new NotSupportedException("The single-threaded test context only supports Post.");
    }

    private static void PumpUntilFinished(Func<Task> body, TimeSpan timeout, TaskCompletionSource<bool> runOutcome)
    {
        var context = new SingleThreadedContext();
        SetSynchronizationContext(context);
        var stopwatch = Stopwatch.StartNew();
        Task bodyTask;
        try
        {
            bodyTask = body();
        }
        catch (Exception exception)
        {
            runOutcome.SetException(exception);
            return;
        }
        //stops the pump once the body finished, wherever its last continuation ran
        bodyTask.ContinueWith(_ => context._pendingCallbacks.CompleteAdding(), TaskScheduler.Default);

        var pollTimeout = TimeSpan.FromMilliseconds(50);
        while (!context._pendingCallbacks.IsCompleted && stopwatch.Elapsed < timeout)
        {
            if (context._pendingCallbacks.TryTake(out var pendingCallback, pollTimeout))
            {
                pendingCallback.Callback(pendingCallback.State);
            }
        }
        context.HandOverRemainingCallbacksToThreadPool();

        if (bodyTask.IsFaulted)
        {
            runOutcome.SetException(bodyTask.Exception!.InnerExceptions);
            return;
        }
        runOutcome.SetResult(bodyTask.IsCompleted);
    }

    //after a timeout the pump is gone; queued and later continuations still run so the timed-out code can wind down
    private void HandOverRemainingCallbacksToThreadPool()
    {
        _pendingCallbacks.CompleteAdding();
        while (_pendingCallbacks.TryTake(out var pendingCallback))
        {
            ThreadPool.QueueUserWorkItem(_ => pendingCallback.Callback(pendingCallback.State));
        }
    }
}
