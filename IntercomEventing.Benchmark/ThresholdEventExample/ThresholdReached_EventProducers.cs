namespace IntercomEventing.Benchmark.ThresholdEventExample;


public class ThresholdReached_IntercomEventProducer
{
    public CounterThresholdReachedEvent ThresholdReachedEvent { get; } = new();
    
    public async Task IncrementCountEvent() => await ThresholdReachedEvent.IncrementValue(1);

   
}

public class ThresholdReached_ClassicEventProducer
{
    public record ThresholdInfo(int Count, int Threshold);
    
    //async friendly version of a classic event
    public event Func<object?, ThresholdInfo, Task>? ThresholdReached;

    public int Count { get; set; }
    public int Threshold { get; set; } = 1;
    
    
    public async Task IncrementCountEvent()
    {
        Count++;
        if(Count < Threshold)
        {
            return;
        }
        await ThresholdReached?.Invoke(this, new(Count, Threshold));
    }
}
