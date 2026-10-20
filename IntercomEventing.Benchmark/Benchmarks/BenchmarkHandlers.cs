namespace IntercomEventing.Benchmark.Benchmarks;

/// <summary>
/// How the subscribed handlers behave in a raise benchmark.
/// </summary>
public enum HandlerBehavior
{
    /// <summary>
    /// Returns an already completed task, like a handler that only updates in-memory state.
    /// </summary>
    Synchronous,

    /// <summary>
    /// Awaits <see cref="Task.Yield"/>, so every handler really runs asynchronously on the thread pool.
    /// </summary>
    Yielding,
}

/// <summary>
/// One raise benchmark case: how many subscribers the events have and how their handlers behave.
/// </summary>
/// <param name="SubscriberCount">Handlers subscribed to each event.</param>
/// <param name="Handler">How each handler completes; meaningless without subscribers.</param>
public sealed record RaiseScenario(int SubscriberCount, HandlerBehavior Handler)
{
    /// <summary>
    /// The text in the benchmark report's Scenario column, such as <c>10 Yielding</c> or <c>0 subscribers</c>.
    /// </summary>
    public override string ToString()
    {
        if (SubscriberCount == 0)
        {
            return "0 subscribers";
        }
        return $"{SubscriberCount} {Handler}";
    }
}

/// <summary>
/// The handlers both event styles subscribe; each pair does the same work for its own parameter type.
/// </summary>
public static class BenchmarkHandlers
{
    public static Task CompleteClassicSynchronously(PriceChanged change)
    {
        return Task.CompletedTask;
    }

    public static async Task YieldClassicAsync(PriceChanged change)
    {
        await Task.Yield();
    }

    public static Task CompleteIntercomSynchronously(EventCall<PriceChanged> call)
    {
        return Task.CompletedTask;
    }

    public static async Task YieldIntercomAsync(EventCall<PriceChanged> call)
    {
        await Task.Yield();
    }
}
