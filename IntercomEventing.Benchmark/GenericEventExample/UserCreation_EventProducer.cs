namespace IntercomEventing.Benchmark.UserCreationExample;

public class UserCreation_EventProducer
{
    public UserCreatedEvent UserCreatedEvent { get; init; } = new();
    public UserThresholdReachedEvent UserThresholdReachedEvent { get; init; } = new();
    

    public async Task CreateUserAsync(User user) => UserCreatedEvent.NotifyUserCreated(user);

    public async Task NotifyUserThresholdReachedAsync(User user) => await UserThresholdReachedEvent.RaiseEvent(new UserThresholdReachedEventCall(user));
}