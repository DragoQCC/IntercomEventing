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
        public int Threshold { get; set; } = 10;
        public DateTime LastEventTime { get; set; }

        public async Task Increment()
        {
            Count++;
            if(Count < Threshold)
            {
                return;
            }
            Stopwatch stopwatch = Stopwatch.StartNew();
            LastEventTime = DateTime.Now;
            /*CounterThresholdReachedEventArgs args = new(Count, Threshold, LastEventTime);
            await ThresholdReachedEvent.RaiseEvent(args,this);
            var timeToInvokeIntercom = stopwatch.Elapsed;
            Console.WriteLine($"Time to invoke intercom event with args: {timeToInvokeIntercom.Milliseconds}ms");
            stopwatch.Restart();*/
            await ThresholdReachedEventNoArgs.RaiseEvent();
            var timeToInvokeIntercomNoArgs = stopwatch.Elapsed;
            Console.WriteLine($"Time to invoke intercom event without args: {timeToInvokeIntercomNoArgs.Milliseconds}ms");
        }
    }

    public class ClassicCounterClass
    {
        public event EventHandler<CounterThresholdReachedEventArgs>? ThresholdReached;

        public int Count { get; set; }
        public int Threshold { get; set; } = 10;
        public DateTime LastEventTime { get; set; }

        public void Increment()
        {
            Count++;
            if(Count < Threshold)
            {
                return;
            }
            Stopwatch stopwatch = Stopwatch.StartNew();
            LastEventTime = DateTime.Now;
            ThresholdReached?.Invoke(this, new(Count, Threshold, LastEventTime));
            var timeToInvokeClassic = stopwatch.Elapsed;
            stopwatch.Restart();
            Console.WriteLine($"Time to invoke classic event: {timeToInvokeClassic.Milliseconds}ms");
        }
    }


    public record CounterThresholdReachedEvent<T> : GenericEvent<CounterThresholdReachedEvent<T>, T> where T : IEventArgs<CounterThresholdReachedEvent<T>>;
    public record struct CounterThresholdReachedEventArgs(int Count, int Threshold, DateTime EventTime) : IEventArgs<CounterThresholdReachedEvent<CounterThresholdReachedEventArgs>>;
    public record CounterThresholdReachedEvent : GenericEvent<CounterThresholdReachedEvent>;


    public class ExampleEventHandler
    {
        public static async ValueTask HandleIntercomEventAsync(CounterThresholdReachedEvent<CounterThresholdReachedEventArgs> @event)
        {
            await Task.Delay(100);
        }

        public static async ValueTask HandleIntercomEventNoArgs(CounterThresholdReachedEvent @event)
        {
            await Task.Delay(100);
        }
        
        public static async ValueTask HandleClassicEventAsync(object? sender, CounterThresholdReachedEventArgs args)
        {
            await Task.Delay(100);
        }
        
        public static void HandleClassicEvent(object? sender, CounterThresholdReachedEventArgs args)
        {
            Task.Delay(100).Wait();
        }
    }
}