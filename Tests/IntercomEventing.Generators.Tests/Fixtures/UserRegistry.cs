namespace IntercomEventing.Generators.Tests.Fixtures;

public sealed record UserCreated(string Name);

public sealed record LoginAttempt(string UserName);

/// <summary>
/// An instance producer in a file-scoped namespace, compiled through the real generator. Its events are raised only from here.
/// </summary>
public sealed partial class UserRegistry
{
    [IntercomEvent]
    private readonly IntercomEvent<UserCreated> _userCreated = new();

    [IntercomEvent(Name = "LoginsExceeded")]
    private readonly ThresholdEvent<int, LoginAttempt> _failedLogins = new(threshold: 3);

    //lets tests check that the generated view property returns the field itself
    internal object UserCreatedBackingEvent => _userCreated;

    public Task CreateAsync(UserCreated user)
    {
        return _userCreated.RaiseAsync(user, caller: this);
    }

    public Task<bool> RecordFailedLoginAsync(LoginAttempt attempt)
    {
        return _failedLogins.IncrementAsync(1, attempt, caller: this);
    }
}
