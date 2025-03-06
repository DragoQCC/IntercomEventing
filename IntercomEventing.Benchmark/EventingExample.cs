using System.Diagnostics;
using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark;

public static class EventingExample
{
    public class IntercomCounterClass
    {
        public CounterThresholdReachedEvent<CounterThresholdReachedEventArgs> ThresholdReachedEvent { get; init; } = new();
        public CounterThresholdReachedEvent ThresholdReachedEventNoArgs { get; init; } = new();

        public int Count { get; set; }
        public int Threshold { get; set; } = 5;
        public DateTime LastEventTime { get; set; }


        public async Task Increment()
        {
            Count++;
            if(Count < Threshold)
            {
                return;
            }
            LastEventTime = DateTime.Now;
            CounterThresholdReachedEventArgs args = new(Count, Threshold, LastEventTime);
            ThresholdReachedEvent.RaiseEvent(args);
        }
    }

    public class ClassicCounterClass
    {
        public event EventHandler<CounterThresholdReachedEventArgs>? ThresholdReached;

        public int Count { get; set; }
        public int Threshold { get; set; } = 5;
        public DateTime LastEventTime { get; set; }
        
        
        public async Task IncrementAsync()
        {
            Count++;
            if(Count < Threshold)
            {
                return;
            }
            LastEventTime = DateTime.Now;
            ThresholdReached?.Invoke(this, new(Count, Threshold, LastEventTime));
        }
        
        public void Increment()
        {
            Count++;
            if(Count < Threshold)
            {
                return;
            }
            LastEventTime = DateTime.Now;
            ThresholdReached?.Invoke(this, new(Count, Threshold, LastEventTime));
        }
    }


    public record CounterThresholdReachedEvent<T> : GenericEvent<CounterThresholdReachedEvent<T>, T> where T : IEventArgs<CounterThresholdReachedEvent<T>>;
    public record struct CounterThresholdReachedEventArgs(int Count, int Threshold, DateTime EventTime) : IEventArgs<CounterThresholdReachedEvent<CounterThresholdReachedEventArgs>>;
    public record CounterThresholdReachedEvent : GenericEvent<CounterThresholdReachedEvent>;


    public class ExampleEventHandler
    {
        public static async ValueTask HandleIntercomEventAsync(CounterThresholdReachedEvent<CounterThresholdReachedEventArgs> @event)
        {
            Console.WriteLine($"Intercom event called async with args: {@event.EventArgs}");
            await Task.Delay(100);
        }
        
        public static async ValueTask HandleClassicEventAsync(object? sender, CounterThresholdReachedEventArgs args)
        {
            Console.WriteLine($"Classic event called async with args: {args}");
            await Task.Delay(100);
        }
        
        public static void HandleClassicEvent(object? sender, CounterThresholdReachedEventArgs args)
        {
            Task.Delay(100).Wait();
        }
        
        public static async ValueTask FaultyIntercomEventHandler(CounterThresholdReachedEvent<CounterThresholdReachedEventArgs> @event)
        {
           //throw an exception at random
           if (Random.Shared.Next(0, 100) > 50)
           {
               throw new Exception("Faulty intercom event handler");
           }
           await Task.Delay(100);
        }
        
        public static async ValueTask FaultyClassicEventHandler(object? sender, CounterThresholdReachedEventArgs args)
        {
            //throw an exception at random
            if (Random.Shared.Next(0, 100) > 50)
            {
                throw new Exception("Faulty classic event handler");
            }
            await Task.Delay(100);
        }
        
       
        //Mimic a long-running event handler
        public static async ValueTask HangingIntercomEventHandler(CounterThresholdReachedEvent<CounterThresholdReachedEventArgs> @event)
        {
            Console.WriteLine($"Intercom async event called at {DateTime.Now}");
            await Task.Delay(5000);
            Console.WriteLine($"Intercom async event finished at {DateTime.Now}");
        }
        
        //Mimic a long-running event handler
        public static async ValueTask HangingClassicEventHandlerAsync(object? sender, CounterThresholdReachedEventArgs args)
        {
            Console.WriteLine($"Classic async event called at {DateTime.Now}");
            await Task.Delay(5000);
            Console.WriteLine($"Classic async event finished at {DateTime.Now}");
        }
        
        public static void HangingClassicEventHandler(object? sender, CounterThresholdReachedEventArgs args)
        { 
            Console.WriteLine($"Classic event called at {DateTime.Now}");
            Thread.Sleep(5000);
            Console.WriteLine($"Classic event finished at {DateTime.Now}");
        }
    }
}