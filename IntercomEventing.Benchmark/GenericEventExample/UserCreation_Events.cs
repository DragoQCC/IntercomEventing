using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark.UserCreationExample;

public record UserCreatedEvent : GenericEvent<UserCreatedEvent>
{
    
    public async Task NotifyUserCreated(User user)
    {
        UserCreatedEventCall eventCall = CreateEventCall(user);
        await RaiseEvent(eventCall);
    }

    /// <inheritdoc />
    override protected UserCreatedEventCall CreateEventCall(params object[]? args)
    {
        if (args?[0] is not User user)
        {
            throw new ArgumentException($"Args[0] is not type {typeof(User)}");
        }
        UserCreatedEventCall eventCall = new(user);
        return eventCall;
    }
}

public record UserCreatedEventCall(User User) : EventCall<UserCreatedEvent>;



public record UserThresholdReachedEvent : GenericEvent<UserThresholdReachedEvent>
{
    
    public void NotifyUserThresholdReached(User user)
    {
        UserThresholdReachedEventCall eventCall = CreateEventCall(user);
        RaiseEvent(eventCall);
    }

    /// <inheritdoc />
    override protected UserThresholdReachedEventCall CreateEventCall(params object[]? args)
    {
        if (args?[0] is User user)
        {
            UserThresholdReachedEventCall eventCall = new(user);
            return eventCall;
        }
        else
        {
            throw new ArgumentException($"Args[0] is not type {typeof(User)}");
        }
    }
}

public record UserThresholdReachedEventCall(User User) : EventCall<UserThresholdReachedEvent>;