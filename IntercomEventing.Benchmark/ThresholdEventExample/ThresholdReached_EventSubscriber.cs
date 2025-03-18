using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark.ThresholdEventExample;

public class ThresholdReached_EventSubscriber
{
    public static async Task HealthyIntercomEventHandlerAsync(CounterThresholdReachedEventCall @event)
    {
        await Task.Delay(100);
    }
    
    public static async Task HealthyClassicEventHandlerAsync(object? sender, ThresholdReached_ClassicEventProducer.ThresholdInfo args)
    {
        await Task.Delay(100);
    }
    
    public static async Task FaultyIntercomEventHandlerAsync(CounterThresholdReachedEventCall @event)
    {
       //throw an exception at random
       if (Random.Shared.Next(0, 100) > 50)
       {
           throw new Exception("Faulty intercom event handler");
       }
       await Task.Delay(100);
    }

    public static async Task FaultyClassicEventHandlerAsync(object? sender, ThresholdReached_ClassicEventProducer.ThresholdInfo args)
    {
        try
        {
            //throw an exception at random
            if (Random.Shared.Next(0, 100) > 50)
            {
                throw new Exception("Faulty classic event handler");
            }
            await Task.Delay(100);
        }
        catch (Exception e)
        {
            //trying to catch an exception from the caller is a pain, so im just going to simulate a performance degradation
            await Task.Delay(1000);
            Console.WriteLine($"Classic event handler failed with error {e.Message}");
        }
    }


    //Mimic a "long-running" event handler
    public static async Task LongRunningIntercomEventHandlerAsync(CounterThresholdReachedEventCall @event)
    {
        await Task.Delay(5000);
    }

    //Mimic a "long-running event" handler
    public static async Task LongRunningClassicEventHandlerAsync(object? sender, ThresholdReached_ClassicEventProducer.ThresholdInfo args)
    {
        await Task.Delay(5000);
    }

    //create hanging event handlers that get stuck in a loop
    public static async Task HangingIntercomEventHandlerAsync(CounterThresholdReachedEventCall @event)
    {
        while (true)
        {
            await Task.Delay(1000);
        }
    }

    public static async Task HangingClassicEventHandlerAsync(object? sender, ThresholdReached_ClassicEventProducer.ThresholdInfo args)
    {
        while (true)
        {
            await Task.Delay(1000);
        }
    }

}