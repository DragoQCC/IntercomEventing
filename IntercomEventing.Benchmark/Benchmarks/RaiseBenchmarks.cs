using BenchmarkDotNet.Attributes;

namespace IntercomEventing.Benchmark.Benchmarks;

/// <summary>
/// Cost of one awaited raise: a classic <c>event Func&lt;T, Task&gt;</c> against an <see cref="IntercomEvent{TPayload}"/>, for
/// 0, 1, 10 and 100 subscribers whose handlers either complete synchronously or yield.
/// </summary>
/// <remarks>
/// <para>The IntercomEvent is measured twice: with the default options (each handler that does not finish synchronously is
/// raced against the one-second <see cref="EventingOptions.StartNextEventHandlerAfter"/> timer) and with that timeout
/// switched off. Both use <see cref="DispatchMode.Parallel"/>, which like the baseline starts every handler before awaiting any.</para>
/// <para>The scenarios come from <see cref="Scenarios"/> rather than two crossed <c>[Params]</c>: without subscribers the
/// handler behaviour changes nothing, so that case is measured once.</para>
/// </remarks>
[MemoryDiagnoser]
public class RaiseBenchmarks
{
    private readonly PriceChanged _priceChange = new("CONTOSO", 42.50m);
    private ClassicPriceFeed _classicFeed = null!;
    private IntercomEvent<PriceChanged> _intercomEvent = null!;
    private IntercomEvent<PriceChanged> _intercomEventWithoutTimeout = null!;

    [ParamsSource(nameof(Scenarios))]
    public RaiseScenario Scenario { get; set; } = null!;

    /// <summary>
    /// No subscribers once, then 1, 10 and 100 subscribers whose handlers complete synchronously or yield.
    /// </summary>
    public static IEnumerable<RaiseScenario> Scenarios()
    {
        yield return new RaiseScenario(SubscriberCount: 0, HandlerBehavior.Synchronous);
        int[] subscriberCounts = [1, 10, 100];
        foreach (var subscriberCount in subscriberCounts)
        {
            yield return new RaiseScenario(subscriberCount, HandlerBehavior.Synchronous);
            yield return new RaiseScenario(subscriberCount, HandlerBehavior.Yielding);
        }
    }

    [GlobalSetup]
    public void SubscribeHandlers()
    {
        _classicFeed = new ClassicPriceFeed();
        //explicit options so the measurement never depends on EventingOptions.Default
        _intercomEvent = new IntercomEvent<PriceChanged>(new EventingOptions());
        _intercomEventWithoutTimeout = new IntercomEvent<PriceChanged>(new EventingOptions { StartNextEventHandlerAfter = null });

        var handlersYield = Scenario.Handler == HandlerBehavior.Yielding;
        Func<PriceChanged, Task> classicHandler = handlersYield
            ? BenchmarkHandlers.YieldClassicAsync
            : BenchmarkHandlers.CompleteClassicSynchronously;
        Func<EventCall<PriceChanged>, Task> intercomHandler = handlersYield
            ? BenchmarkHandlers.YieldIntercomAsync
            : BenchmarkHandlers.CompleteIntercomSynchronously;

        for (var subscriberNumber = 0; subscriberNumber < Scenario.SubscriberCount; subscriberNumber++)
        {
            _classicFeed.Changed += classicHandler;
            _intercomEvent.Raised += intercomHandler;
            _intercomEventWithoutTimeout.Raised += intercomHandler;
        }
    }

    [GlobalCleanup]
    public async Task DisposeEventsAsync()
    {
        await _intercomEvent.DisposeAsync();
        await _intercomEventWithoutTimeout.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public Task ClassicEvent()
    {
        return _classicFeed.RaiseAsync(_priceChange);
    }

    [Benchmark]
    public Task IntercomEventDefaultOptions()
    {
        return _intercomEvent.RaiseAsync(_priceChange);
    }

    [Benchmark]
    public Task IntercomEventWithoutTimeout()
    {
        return _intercomEventWithoutTimeout.RaiseAsync(_priceChange);
    }
}
