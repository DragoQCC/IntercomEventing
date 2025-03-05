using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark;

public static class EventingExample
{
    public class IntercomCounterClass
    {
        public CounterThresholdReachedEvent<CounterThresholdReachedEventArgs> ThresholdReachedEvent { get; init; } = new();

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
            LastEventTime = DateTime.Now;
            CounterThresholdReachedEventArgs args = new(Count, Threshold, LastEventTime);
            await ThresholdReachedEvent.RaiseEvent(args,this);
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
            LastEventTime = DateTime.Now;
            ThresholdReached?.Invoke(this, new(Count, Threshold, LastEventTime));
        }
    }


    public record CounterThresholdReachedEvent<T> : GenericEvent<CounterThresholdReachedEvent<T>, T> where T : IEventArgs<CounterThresholdReachedEvent<T>>;
    public record struct CounterThresholdReachedEventArgs(int Count, int Threshold, DateTime EventTime) : IEventArgs<CounterThresholdReachedEvent<CounterThresholdReachedEventArgs>>;

    //example of making a different set of arguments for the same event
    public record struct CounterThresholdReachedEventArgs2(int Count) : IEventArgs<CounterThresholdReachedEvent<CounterThresholdReachedEventArgs2>>;


    public class ExampleEventHandler
    {
        public static async ValueTask HandleIntercomEvent(CounterThresholdReachedEvent<CounterThresholdReachedEventArgs> @event)
        {
            Console.WriteLine($"Intercom event called at {@event.Metadata.LastEventTime} by {@event.Metadata.EventCaller}");
            await Task.Delay(1000);
        }

        //Currently this is sleeping the calling thread, not exactly a fair comparison but events also are not asnyc
        public static async ValueTask HandleClassicEvent(object? sender, CounterThresholdReachedEventArgs args)
        {
            Console.WriteLine($"Classic event called at {args.EventTime} by {sender}");
            await Task.Delay(1000);
        }
    }
}