using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark.ThresholdEventExample;

//public record CounterThresholdReachedEvent : GenericEvent<CounterThresholdReachedEvent>;
//public record CounterThresholdReachedEventCall(int Threshold, int CurrentValue) : EventCall<CounterThresholdReachedEvent>;

public record CounterThresholdReachedEvent() : ThresholdEvent<CounterThresholdReachedEvent, int>(1)
{
    protected override ThresholdEventCall<CounterThresholdReachedEvent, int> CreateThresholdEventCall(int threshold, int currentValue) => new CounterThresholdReachedEventCall(threshold, currentValue);
}

public record CounterThresholdReachedEventCall(int Threshold, int CurrentValue) : ThresholdEventCall<CounterThresholdReachedEvent,int>(Threshold, CurrentValue);



