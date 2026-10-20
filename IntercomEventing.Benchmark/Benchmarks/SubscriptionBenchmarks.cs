using BenchmarkDotNet.Attributes;

namespace IntercomEventing.Benchmark.Benchmarks;

/// <summary>
/// Cost of adding and then removing one handler, on an event that already has 0 or 100 subscribers. Both styles copy their
/// handler list on every change, so the cost grows with the number of existing subscribers.
/// </summary>
[MemoryDiagnoser]
public class SubscriptionBenchmarks
{
    //cached so no benchmark pays for creating the delegate
    private readonly Func<PriceChanged, Task> _classicHandler = BenchmarkHandlers.CompleteClassicSynchronously;
    private readonly Func<EventCall<PriceChanged>, Task> _intercomHandler = BenchmarkHandlers.CompleteIntercomSynchronously;

    private readonly SubscriptionOptions<EventCall<PriceChanged>> _hooks = new()
    {
        OnSubscribed = static _ => Task.CompletedTask,
        OnUnsubscribed = static _ => Task.CompletedTask,
    };

    private ClassicPriceFeed _classicFeed = null!;
    private IntercomEvent<PriceChanged> _intercomEvent = null!;

    [Params(0, 100)]
    public int ExistingSubscribers { get; set; }

    [GlobalSetup]
    public void AddExistingSubscribers()
    {
        _classicFeed = new ClassicPriceFeed();
        _intercomEvent = new IntercomEvent<PriceChanged>(new EventingOptions());
        for (var subscriberNumber = 0; subscriberNumber < ExistingSubscribers; subscriberNumber++)
        {
            _classicFeed.Changed += BenchmarkHandlers.YieldClassicAsync;
            _intercomEvent.Raised += BenchmarkHandlers.YieldIntercomAsync;
        }
    }

    [GlobalCleanup]
    public async Task DisposeEventAsync()
    {
        await _intercomEvent.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public void ClassicEventAddThenRemove()
    {
        _classicFeed.Changed += _classicHandler;
        _classicFeed.Changed -= _classicHandler;
    }

    [Benchmark]
    public void IntercomEventRaisedAddThenRemove()
    {
        _intercomEvent.Raised += _intercomHandler;
        _intercomEvent.Raised -= _intercomHandler;
    }

    [Benchmark]
    public void IntercomEventSubscribeThenDispose()
    {
        var subscription = _intercomEvent.Subscribe(_intercomHandler);
        subscription.Dispose();
    }

    [Benchmark]
    public async Task IntercomEventSubscribeAsyncWithHooksThenDisposeAsync()
    {
        var subscription = await _intercomEvent.SubscribeAsync(_intercomHandler, _hooks);
        await subscription.DisposeAsync();
    }
}
