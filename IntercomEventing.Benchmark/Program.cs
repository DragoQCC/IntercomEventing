

using BenchmarkDotNet.Running;

namespace IntercomEventing.Benchmark;

class Program
{
    static async Task Main(string[] args)
    {
        //await CreateAndRunEvents();
        BenchmarkRunner.Run<EventBenchmarks>();
    }

    public static async Task CreateAndRunEvents()
    {
        Console.WriteLine("Subscribing to an intercom event");
        EventingExample.IntercomCounterClass intercomCounterClass = new EventingExample.IntercomCounterClass();
        await intercomCounterClass.ThresholdReachedEvent.Subscribe(EventingExample.ExampleEventHandler.HandleIntercomEvent);

        Console.WriteLine("Subscribing to a classic event");
        EventingExample.ClassicCounterClass classicCounterClass = new EventingExample.ClassicCounterClass();
        classicCounterClass.ThresholdReached += async (o,e) => await EventingExample.ExampleEventHandler.HandleClassicEvent(o,e);

        for(int i = 0; i < 1000; i++)
        {
            Console.WriteLine($"Incrementing counter to {i}");
            await intercomCounterClass.Increment();
            classicCounterClass.Increment();
        }
    }
}