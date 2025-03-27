using BenchmarkDotNet.Attributes;
using IntercomEventing.Benchmark.ThresholdEventExample;

namespace IntercomEventing.Benchmark;

[MemoryDiagnoser]
[HideColumns("Job")]
public class EventBenchmarks
{
    
    [Benchmark]
    public async Task IntercomEvent()
    {
       ThresholdReached_IntercomEventProducer intercomEventClass = new();
        for (int i = 0; i < Program.NUM_SUBSCRIBERS; i++)
        {
            await intercomEventClass.ThresholdReachedEvent.Subscribe<CounterThresholdReachedEventCall>(ThresholdReached_EventSubscriber.HealthyIntercomEventHandlerAsync);
        }
        for(int i = 0; i < Program.NUM_EVENT_EXEC; i++)
        {
            await intercomEventClass.IncrementCountEvent();
        }
    }

    [Benchmark(Baseline = true)]
    public async Task ClassicEvent()
    {
        ThresholdReached_ClassicEventProducer classicEventClass = new();
        for (int i = 0; i < Program.NUM_SUBSCRIBERS; i++)
        {
            classicEventClass.ThresholdReached += async (o,e) => await ThresholdReached_EventSubscriber.HealthyClassicEventHandlerAsync(o,e);
        }
        for(int i = 0; i < Program.NUM_EVENT_EXEC; i++)
        {
            await classicEventClass.IncrementCountEvent();
        }
    }

}