namespace IntercomEventing.Benchmark.Benchmarks;

/// <summary>
/// The payload every benchmark raises.
/// </summary>
public sealed record PriceChanged(string Symbol, decimal Price);

/// <summary>
/// The baseline: a native <c>event Func&lt;T, Task&gt;</c>, raised the usual correct way, by invoking each handler and awaiting
/// all of their tasks.
/// </summary>
/// <remarks>
/// It does only that: no per-raise call object or metadata, no exception isolation (a handler that throws synchronously stops
/// the raise and later handlers never run), no handler timeouts, no concurrency limit and no subscription handles.
/// </remarks>
public sealed class ClassicPriceFeed
{
    public event Func<PriceChanged, Task>? Changed;

    public Task RaiseAsync(PriceChanged change)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return Task.CompletedTask;
        }
        //invoking the multicast delegate directly would return only the last handler's task, so each handler is invoked on its own
        var invocationList = handlers.GetInvocationList();
        var handlerTasks = new Task[invocationList.Length];
        for (var index = 0; index < invocationList.Length; index++)
        {
            var handler = (Func<PriceChanged, Task>)invocationList[index];
            handlerTasks[index] = handler(change);
        }
        return Task.WhenAll(handlerTasks);
    }
}
