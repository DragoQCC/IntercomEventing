using BenchmarkDotNet.Attributes;

namespace IntercomEventing.Benchmark;

[MemoryDiagnoser]
[HideColumns(new []{"Job"})] //"Error", "StdDev","Median","Gen1","Gen2"
public class EventBenchmarks
{

    static int _iterations = 15;

    [Benchmark]
    public async Task IntercomEvent()
    {
        //create our eventing class
        EventingExample.IntercomCounterClass intercomCounterClass = new EventingExample.IntercomCounterClass();

        await intercomCounterClass.ThresholdReachedEvent.Subscribe(EventingExample.ExampleEventHandler.HandleIntercomEvent);

        //call the event
        for(int i = 0; i < _iterations; i++)
        {
            await intercomCounterClass.Increment();
        }
    }

    [Benchmark]
    public async Task ClassicEvent()
    {
        //create our eventing class
        EventingExample.ClassicCounterClass classicCounterClass = new EventingExample.ClassicCounterClass();

        //subscribe to the event
        classicCounterClass.ThresholdReached += async (o,e) => await EventingExample.ExampleEventHandler.HandleClassicEvent(o,e);

        //call the event
        for(int i = 0; i < _iterations; i++)
        {
            classicCounterClass.Increment();
        }
    }

}