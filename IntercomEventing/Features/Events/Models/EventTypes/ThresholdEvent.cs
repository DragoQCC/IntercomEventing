using System.Numerics;
using HelpfulTypesAndExtensions;

namespace IntercomEventing.Features.Events;

/// <summary>
/// Represents an event that tracks a value and raises an event when the value reaches a threshold <br/>
/// Can take any type that implements IComparable and INumber <br/>
/// </summary>
/// <typeparam name="TEvent"></typeparam>
/// <typeparam name="TValue"></typeparam>
public abstract record ThresholdEvent<TEvent, TValue> : GenericEvent<TEvent>
    where TEvent : ThresholdEvent<TEvent, TValue>
    where TValue : IComparable<TValue>, INumber<TValue>
{
    public TValue CurrentValue { get; private set; } = default!;
    public TValue Threshold { get; }
    private readonly Func<TValue, TValue, bool> _thresholdCheck;

    

    /// <summary>
    /// Represents an event that tracks a value and raises an event when the value reaches a threshold <br/>
    /// Can take any type that implements IComparable and INumber <br/>
    /// Also takes an optional threshold check function that can be used to customize the threshold check
    /// </summary>
    /// <param name="threshold"> The threshold value, the event will be raised when the value reaches this threshold or exceeds it </param>
    /// <param name="thresholdCheck"> The threshold check function that determines when the threshold is reached (Optional) </param>
    protected ThresholdEvent(TValue threshold, Func<TValue, TValue, bool>? thresholdCheck = null)
    {
        Threshold = threshold;
        _thresholdCheck = thresholdCheck ?? ((current, threshold) => current.CompareTo(threshold) >= 0);
    }

    /// <summary>
    /// Increments the value of the event and raises the event if the threshold is reached
    /// </summary>
    /// <param name="increment"></param>
    public async Task IncrementValue(TValue increment)
    {
        CurrentValue += increment;
        if (_thresholdCheck(CurrentValue, Threshold))
        {
            DebugHelp.DebugWriteLine($"Threshold reached: {CurrentValue} >= {Threshold}");
            ThresholdEventCall<TEvent, TValue> eventCall = CreateEventCall();
            eventCall.Threshold = Threshold;
            eventCall.CurrentValue = CurrentValue;
            await RaiseEvent(eventCall);
        }
    }

    ///<inheritdoc/>
    override abstract protected ThresholdEventCall<TEvent, TValue> CreateEventCall(params object[]? args);
}

/// <summary>
/// The event call for a threshold event <br/>
/// Should be used to pass data to event handlers <br/>
/// Includes the current value and the threshold value
/// </summary>
/// <typeparam name="TEvent"></typeparam>
/// <typeparam name="TValue"></typeparam>
public record ThresholdEventCall<TEvent, TValue> : EventCall<TEvent>
where TEvent : ThresholdEvent<TEvent, TValue>
where TValue : IComparable<TValue>, INumber<TValue>
{
    public TValue Threshold { get; internal set; } = default!; 
    public TValue CurrentValue { get; internal set; } = default!; 
}