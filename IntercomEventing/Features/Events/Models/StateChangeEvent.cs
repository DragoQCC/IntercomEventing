namespace IntercomEventing.Features.Events;

public abstract record StateChangeEvent<TEvent, TState> : GenericEvent<TEvent>
    where TEvent : StateChangeEvent<TEvent, TState>
    where TState : IEquatable<TState>
{
    private TState _currentState;

    public TState CurrentState
    {
        get => _currentState;
        set
        {
            if (!_currentState.Equals(value))
            {
                var oldState = _currentState;
                _currentState = value;
                OnStateChanged(oldState, value);
            }
        }
    }

    protected StateChangeEvent(TState initialState)
    {
        _currentState = initialState;
    }

    protected virtual async void OnStateChanged(TState oldState, TState newState)
    {
        await RaiseEvent<StateChangeEvent<TEvent, TState>>(this);
    }
}