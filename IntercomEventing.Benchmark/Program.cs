

using System.Diagnostics;
using BenchmarkDotNet.Running;
using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark;

class Program
{
    static async Task Main(string[] args)
    {
        await CreateAndRunEvents();
        //BenchmarkRunner.Run<EventBenchmarks>();
    }

    public static async Task CreateAndRunEvents()
    {
        const int NUM_SUBSCRIBERS = 10;
        const int NUM_EVENTS = 50;
        
        Stopwatch stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"\nCreating and subscribing {NUM_SUBSCRIBERS} subscribers to an intercom event");
        EventingExample.IntercomCounterClass intercomCounterClass = new();
        for (int i = 0; i < NUM_SUBSCRIBERS; i++)
        {
            await intercomCounterClass.ThresholdReachedEvent.Subscribe(EventingExample.ExampleEventHandler.HangingIntercomEventHandler);
        }
        var timeToSubscribeIntercom = stopwatch.Elapsed;
        
        stopwatch.Restart();
        Console.WriteLine($"Creating and subscribing {NUM_SUBSCRIBERS} subscribers to a classic event");
        EventingExample.ClassicCounterClass classicCounterClass = new();
        for (int i = 0; i < NUM_SUBSCRIBERS; i++)
        {
            //classicCounterClass.ThresholdReached += async (o,e) => await EventingExample.ExampleEventHandler.HangingClassicEventHandler(o,e);
            classicCounterClass.ThresholdReached += EventingExample.ExampleEventHandler.HangingClassicEventHandler;
        }
        var timeToSubscribeClassic = stopwatch.Elapsed;
        
        Console.WriteLine($"\nTime to subscribe {NUM_SUBSCRIBERS} intercom event handlers -> {timeToSubscribeIntercom.Seconds}s:{timeToSubscribeIntercom.Milliseconds:D3}ms:{timeToSubscribeIntercom.Microseconds:D3}us");
        Console.WriteLine($"Time to subscribe {NUM_SUBSCRIBERS} classic event handlers -> {timeToSubscribeClassic.Seconds}s:{timeToSubscribeClassic.Milliseconds:D3}ms:{timeToSubscribeClassic.Microseconds:D3}us");
        
        Console.WriteLine($"\nRunning {NUM_EVENTS} events for each type...\n");
        
        stopwatch.Restart();
        for(int i = 0; i < NUM_EVENTS; i++)
        {
            await intercomCounterClass.Increment();
        }
        var timeToRunIntercom = stopwatch.Elapsed;
        
        stopwatch.Restart();
        for(int i = 0; i < NUM_EVENTS; i++)
        {
            classicCounterClass.Increment();
        }
        var timeToRunClassic = stopwatch.Elapsed;
        
        Console.WriteLine($"Time to run {NUM_EVENTS} intercom events -> {timeToRunIntercom.Seconds}s:{timeToRunIntercom.Milliseconds:D3}ms:{timeToRunIntercom.Microseconds:D3}us");
        Console.WriteLine($"Time to run {NUM_EVENTS} classic events -> {timeToRunClassic.Seconds}s:{timeToRunClassic.Milliseconds:D3}ms:{timeToRunClassic.Microseconds:D3}us");
    }
}