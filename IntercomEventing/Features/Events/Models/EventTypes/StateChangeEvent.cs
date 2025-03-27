namespace IntercomEventing.Features.Events;

/// <summary>
/// Represents an event that tracks state changes <br/>
/// The event will only be raised when the state changes
/// </summary>
/// <typeparam name="TEvent"> The type of event that is being raised </typeparam>
/// <typeparam name="TState"> The type of state that is being tracked </typeparam>
public abstract record StateChangeEvent<TEvent, TState> : GenericEvent<TEvent>
    where TEvent : StateChangeEvent<TEvent, TState>
    where TState : IEquatable<TState>
{
    private TState _currentState;

    /// <summary>
    /// The current state of the event <br/>
    /// Setting the state will raise the event if the state has changed
    /// </summary>
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

    virtual protected async Task OnStateChanged(TState oldState, TState newState)
    {
        StateChangeEventCall<TEvent, TState> eventCall =  CreateEventCall();
        eventCall.OldState = oldState;
        eventCall.NewState = newState;
        await RaiseEvent(eventCall);
    }

    override abstract protected StateChangeEventCall<TEvent, TState> CreateEventCall();
}

/// <summary>
/// The event call for a state change event <br/>
/// Should be used to pass data to event handlers <br/>
/// Includes the old and new state of the event
/// </summary>
/// <typeparam name="TEvent"></typeparam>
/// <typeparam name="TState"></typeparam>
public record StateChangeEventCall<TEvent, TState> : EventCall<TEvent>
    where TEvent : StateChangeEvent<TEvent, TState>
    where TState : IEquatable<TState>
    {
        public TState OldState { get; internal set; } = default!; 
        public TState NewState { get; internal set; } = default!;

        protected StateChangeEventCall() { }
    }