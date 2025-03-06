using BenchmarkDotNet.Attributes;

namespace IntercomEventing.Benchmark;

[MemoryDiagnoser]
[HideColumns(new []{"Job"})] //"Error", "StdDev","Median","Gen1","Gen2"
public class EventBenchmarks
{

    static int _iterations = 15;
    static int Subscriptions = 5;

    /*[Benchmark]
    public async Task IntercomEvent()
    {
        //create our eventing class
        EventingExample.IntercomCounterClass intercomCounterClass = new EventingExample.IntercomCounterClass();

        //subscribe to the event with args using an Async Handler
        for(int i = 0; i < Subscriptions; i++)
        {
            await intercomCounterClass.ThresholdReachedEvent.Subscribe(EventingExample.ExampleEventHandler.HandleIntercomEventAsync);
        }

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

        //subscribe to the event for each subscription
        for(int i = 0; i < Subscriptions; i++)
        {
            classicCounterClass.ThresholdReached += async (o,e) => await EventingExample.ExampleEventHandler.HandleClassicEventAsync(o,e);
        }
       

        //call the event
        for(int i = 0; i < _iterations; i++)
        {
            await classicCounterClass.Increment();
        }
    }*/
    
    /*[Benchmark]
    public async Task IntercomEvent_Faulty()
    {
        //create our eventing class
        EventingExample.IntercomCounterClass intercomCounterClass = new EventingExample.IntercomCounterClass();

        //subscribe to the event with args using an Async Handler
        for(int i = 0; i < Subscriptions; i++)
        {
            await intercomCounterClass.ThresholdReachedEvent.Subscribe(EventingExample.ExampleEventHandler.FaultyIntercomEventHandler);
        }

        //call the event
        for(int i = 0; i < _iterations; i++)
        {
            await intercomCounterClass.Increment();
        }
    }

    [Benchmark]
    public async Task ClassicEvent_Faulty()
    {
        //im not sure if this will even catch exceptions thrown by the event handler
        try
        {
            //create our eventing class
            EventingExample.ClassicCounterClass classicCounterClass = new EventingExample.ClassicCounterClass();

            //subscribe to the event for each subscription
            for(int i = 0; i < Subscriptions; i++)
            {
                classicCounterClass.ThresholdReached += async (o,e) => await EventingExample.ExampleEventHandler.FaultyClassicEventHandler(o,e);
            }
       

            //call the event
            for(int i = 0; i < _iterations; i++)
            {
                await classicCounterClass.Increment();
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }
        
    }*/
    
    [Benchmark]
    public async Task IntercomEvent_Hanging()
    {
        //create our eventing class
        EventingExample.IntercomCounterClass intercomCounterClass = new EventingExample.IntercomCounterClass();
        //subscribe to the event with args using an Async Handler
        for(int i = 0; i < Subscriptions; i++)
        {
            await intercomCounterClass.ThresholdReachedEvent.Subscribe(EventingExample.ExampleEventHandler.HangingIntercomEventHandler);
        }
        //call the event
        for(int i = 0; i < _iterations; i++)
        {
            await intercomCounterClass.Increment();
        }
    }
    
    [Benchmark]
    public async Task ClassicEvent_Hanging()
    {
        //create our eventing class
        EventingExample.ClassicCounterClass classicCounterClass = new EventingExample.ClassicCounterClass();
        //subscribe to the event for each subscription
        for(int i = 0; i < Subscriptions; i++)
        {
            classicCounterClass.ThresholdReached += async (o,e) => await EventingExample.ExampleEventHandler.HangingClassicEventHandlerAsync(o,e);
        }
        //call the event
        for(int i = 0; i < _iterations; i++)
        {
            await classicCounterClass.IncrementAsync();
        }
    }

}