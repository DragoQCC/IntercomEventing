using HelpfulTypesAndExtensions;

namespace IntercomEventing.Features.Events;

public abstract record ThresholdEvent<TEvent, TValue> : GenericEvent<TEvent>
    where TEvent : ThresholdEvent<TEvent, TValue>
    where TValue : IComparable<TValue>
{
    private TValue _currentValue;
    public TValue Threshold { get; init; }
    private readonly Func<TValue, TValue, bool> _thresholdCheck;

    public TValue CurrentValue
    {
        get => _currentValue;
        set
        {
            _currentValue = value;
            if (_thresholdCheck(_currentValue, Threshold))
            {
                DebugHelp.DebugWriteLine($"Threshold reached: {_currentValue} >= {Threshold}");
                // Allow derived classes to create their specific event call type
                RaiseEvent(CreateThresholdEventCall(Threshold, _currentValue)).GetAwaiter().GetResult();
            }
        }
    }

    protected ThresholdEvent(TValue threshold, Func<TValue, TValue, bool>? thresholdCheck = null)
    {
        Threshold = threshold;
        _thresholdCheck = thresholdCheck ?? ((current, threshold) => current.CompareTo(threshold) >= 0);
    }

    // Virtual method to allow derived classes to create their specific event call type
    protected virtual ThresholdEventCall<TEvent, TValue> CreateThresholdEventCall(TValue threshold, TValue currentValue)
    {
        return new ThresholdEventCall<TEvent, TValue>(threshold, currentValue);
    }
}

public record ThresholdEventCall<TEvent,TValue>(TValue Threshold, TValue CurrentValue) : EventCall<TEvent>
where TEvent : ThresholdEvent<TEvent, TValue>
where TValue : IComparable<TValue>;