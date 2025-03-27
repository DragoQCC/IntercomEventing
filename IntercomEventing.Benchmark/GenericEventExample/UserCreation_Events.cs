using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark.UserCreationExample;

public record UserCreatedEvent : GenericEvent<UserCreatedEvent>
{
    private User _user;
    
    public void NotifyUserCreated(User user)
    {
        _user = user;
        UserCreatedEventCall eventCall = CreateEventCall();
        RaiseEvent(eventCall);
    }
    
    /// <inheritdoc />
    override protected UserCreatedEventCall CreateEventCall() => new(_user);
}

public record UserCreatedEventCall(User User) : EventCall<UserCreatedEvent>;



public record UserThresholdReachedEvent : GenericEvent<UserThresholdReachedEvent>
{
    private User _user;
    
    public void NotifyUserThresholdReached(User user)
    {
        _user = user;
        UserThresholdReachedEventCall eventCall = CreateEventCall();
        RaiseEvent(eventCall);
    }

    /// <inheritdoc />
    override protected UserThresholdReachedEventCall CreateEventCall() => new(_user);
}

public record UserThresholdReachedEventCall(User User) : EventCall<UserThresholdReachedEvent>;