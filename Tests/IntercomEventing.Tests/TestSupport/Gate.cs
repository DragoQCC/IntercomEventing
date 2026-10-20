namespace IntercomEventing.Tests.TestSupport;

/// <summary>
/// Creates the task completion sources that tests use to hold a handler or hook at a known point and release it later.
/// </summary>
/// <remarks>
/// Continuations run asynchronously, so releasing a gate never runs the waiting handler inline on the test's thread.
/// </remarks>
internal static class Gate
{
    public static TaskCompletionSource Create()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static TaskCompletionSource<TResult> Create<TResult>()
    {
        return new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
