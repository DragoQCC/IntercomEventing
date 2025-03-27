namespace IntercomEventing.Benchmark.UserCreationExample;

public class UserCreation_EventProducer
{
    public UserCreatedEvent UserCreatedEvent { get; init; } = new();
    public UserThresholdReachedEvent UserThresholdReachedEvent { get; init; } = new();

    public async Task CreateUserAsync(User user) => await UserCreatedEvent.RaiseEvent(new UserCreatedEventCall(user),this);

    public async Task NotifyUserThresholdReachedAsync(User user) => await UserThresholdReachedEvent.RaiseEvent(new UserThresholdReachedEventCall(user));
}