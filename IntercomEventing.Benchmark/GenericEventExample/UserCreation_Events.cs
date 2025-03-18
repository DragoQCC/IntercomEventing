using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark.UserCreationExample;

public record UserCreatedEvent : GenericEvent<UserCreatedEvent>;

public record UserCreatedEventCall(User User) : EventCall<UserCreatedEvent>;



public record UserThresholdReachedEvent : GenericEvent<UserThresholdReachedEvent>;

public record UserThresholdReachedEventCall(User User) : EventCall<UserThresholdReachedEvent>;