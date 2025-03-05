

using System.Diagnostics;
using BenchmarkDotNet.Running;
using IntercomEventing.Features.Events;

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
        
        Stopwatch stopwatch = Stopwatch.StartNew();
        Console.WriteLine("Subscribing to an intercom event");
        EventingExample.IntercomCounterClass intercomCounterClass = new EventingExample.IntercomCounterClass();
        await intercomCounterClass.ThresholdReachedEvent.Subscribe(EventingExample.ExampleEventHandler.HandleIntercomEventAsync);
        var timeToSubscribeIntercom = stopwatch.Elapsed;
        
        await intercomCounterClass.ThresholdReachedEventNoArgs.Subscribe(EventingExample.ExampleEventHandler.HandleIntercomEventNoArgs);
        
        stopwatch.Restart();
        Console.WriteLine("Subscribing to a classic event");
        EventingExample.ClassicCounterClass classicCounterClass = new EventingExample.ClassicCounterClass();
        classicCounterClass.ThresholdReached += async (o,e) => await EventingExample.ExampleEventHandler.HandleClassicEventAsync(o,e);
        var timeToSubscribeClassic = stopwatch.Elapsed;
        stopwatch.Restart();
        
        Console.WriteLine($"Time to subscribe to an intercom event: {timeToSubscribeIntercom.Milliseconds}ms ");
        Console.WriteLine($"Time to subscribe to classic event:{timeToSubscribeClassic.Milliseconds}ms");

        for(int i = 0; i < 50; i++)
        {
            Console.WriteLine($"Incrementing counter to {i}");
            await intercomCounterClass.Increment();
            classicCounterClass.Increment();
        }
    }
}