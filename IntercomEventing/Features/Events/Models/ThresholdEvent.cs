namespace IntercomEventing.Features.Events;

public abstract record ThresholdEvent<TEvent, TValue> : GenericEvent<TEvent>
    where TEvent : ThresholdEvent<TEvent, TValue>
    where TValue : IComparable<TValue>
{
    private TValue _currentValue;
    private readonly TValue _threshold;
    private readonly Func<TValue, TValue, bool> _thresholdCheck;

    public TValue CurrentValue
    {
        get => _currentValue;
        set
        {
            _currentValue = value;
            if (_thresholdCheck(_currentValue, _threshold))
            {
                RaiseEvent<ThresholdEvent<TEvent, TValue>>(this).GetAwaiter().GetResult();
            }
        }
    }

    protected ThresholdEvent(TValue threshold, Func<TValue, TValue, bool>? thresholdCheck = null)
    {
        _threshold = threshold;
        _thresholdCheck = thresholdCheck ?? ((current, threshold) => current.CompareTo(threshold) >= 0);
    }
}