namespace IntercomEventing.Generators.Tests.Fixtures;

public sealed record UserJoined(string Name);

/// <summary>
/// A producer whose field name already ends in <c>Event</c>, compiled through the real generator: the generated members are
/// <c>UserJoined</c> and <c>UserJoinedEvent</c>, not <c>UserJoinedEvent</c> and <c>UserJoinedEventEvent</c>.
/// </summary>
public sealed partial class ChatRoom
{
    [IntercomEvent]
    private readonly IntercomEvent<UserJoined> _userJoinedEvent = new();

    //lets tests check that the generated view property returns the field itself
    internal object UserJoinedBackingEvent => _userJoinedEvent;

    public Task JoinAsync(UserJoined user)
    {
        return _userJoinedEvent.RaiseAsync(user, caller: this);
    }
}
