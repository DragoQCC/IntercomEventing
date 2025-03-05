using BenchmarkDotNet.Attributes;

namespace IntercomEventing.Benchmark;

[MemoryDiagnoser]
[HideColumns(new []{"Job"})] //"Error", "StdDev","Median","Gen1","Gen2"
public class EventBenchmarks
{

    static int _iterations = 100;

    [Benchmark]
    public async Task IntercomEvent_10Subs_100Calls()
    {
        //create our eventing class
        EventingExample.IntercomCounterClass intercomCounterClass = new EventingExample.IntercomCounterClass();
        //subscribe to the event 10 times
        for(int i = 0; i < 10; i++)
        {
            await intercomCounterClass.ThresholdReachedEvent.Subscribe(EventingExample.ExampleEventHandler.HandleIntercomEvent);
        }
        //call the event
        for(int i = 0; i < _iterations; i++)
        {
            await intercomCounterClass.Increment();
        }
    }

    [Benchmark]
    public async Task ClassicEvent_10Subs_100Calls()
    {
        //create our eventing class
        EventingExample.ClassicCounterClass classicCounterClass = new EventingExample.ClassicCounterClass();
        //subscribe to the event 10 times
        for(int i = 0; i < 10; i++)
        {
            classicCounterClass.ThresholdReached += async (o,e) => await EventingExample.ExampleEventHandler.HandleClassicEvent(o,e);
        }
        //call the event
        for(int i = 0; i < _iterations; i++)
        {
            classicCounterClass.Increment();
        }
    }

}