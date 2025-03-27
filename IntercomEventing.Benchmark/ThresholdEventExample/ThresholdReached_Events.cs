using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark.ThresholdEventExample;

public record CounterReachedGenericEvent : GenericEvent<CounterReachedGenericEvent>
{
    public int Threshold { get; init; } = 1;
    public int CurrentValue { get; set; } = 0;

    public async Task IncrementValue(int value)
    {
        CurrentValue += value;
        if(CurrentValue < Threshold)
        {
            return;
        }
        await RaiseEvent(CreateEventCall());
    }
    
    /// <inheritdoc />
    override protected EventCall<CounterReachedGenericEvent> CreateEventCall() => new CounterReachedGenericEventCall(Threshold, CurrentValue);
}

public record CounterReachedGenericEventCall(int Threshold, int CurrentValue) : EventCall<CounterReachedGenericEvent>;


public record CounterThresholdReachedEvent() : ThresholdEvent<CounterThresholdReachedEvent, int>(1)
{
    protected override ThresholdEventCall<CounterThresholdReachedEvent, int> CreateEventCall() => new CounterThresholdReachedEventCall();
}

public record CounterThresholdReachedEventCall : ThresholdEventCall<CounterThresholdReachedEvent,int>;



