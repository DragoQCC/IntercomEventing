namespace IntercomEventing.Benchmark.Examples.Generic;

/// <summary>
/// The payload of <see cref="UserRegistry.UserCreated"/>. A record keeps it immutable, which matters because every handler of
/// one raise receives the same instance.
/// </summary>
public sealed record UserCreated(string Name, string Email);

/// <summary>
/// The payload of <see cref="UserRegistry.UserRemoved"/>.
/// </summary>
public sealed record UserRemoved(string Name);

/// <summary>
/// The producer: it owns its events, raises them after its own work is done and disposes them.
/// </summary>
public sealed class UserRegistry : IAsyncDisposable
{
    private readonly Lock _usersLock = new();
    private readonly Dictionary<string, UserCreated> _usersByName = new(StringComparer.OrdinalIgnoreCase);

    //only the registry can raise UserRemoved: subscribers see it through the subscribe-only view below
    private readonly IntercomEvent<UserRemoved> _userRemoved = new();

    /// <summary>
    /// Raised after a user is stored. Anyone holding the registry can subscribe and, because this is the event itself,
    /// also raise it. <c>Name</c> replaces the default display name <c>IntercomEvent&lt;UserCreated&gt;</c> in the metadata.
    /// </summary>
    public IntercomEvent<UserCreated> UserCreated { get; } = new() { Name = "UserCreated" };

    /// <summary>
    /// Raised after a user is removed. The subscribe-only view has <c>Raised</c>, <c>Subscribe</c> and <c>SubscribeAsync</c>,
    /// but no <c>RaiseAsync</c>.
    /// </summary>
    public IIntercomEvent<EventCall<UserRemoved>> UserRemoved => _userRemoved;

    /// <summary>
    /// A payload-less event: handlers learn only that it happened, plus the metadata.
    /// </summary>
    public IntercomEvent Cleared { get; } = new();

    public async Task CreateAsync(string name, string email)
    {
        var user = new UserCreated(name, email);
        lock (_usersLock)
        {
            _usersByName.Add(name, user);
        }
        await UserCreated.RaiseAsync(user, caller: this);
    }

    public async Task RemoveAsync(string name)
    {
        bool removed;
        lock (_usersLock)
        {
            removed = _usersByName.Remove(name);
        }
        if (removed)
        {
            await _userRemoved.RaiseAsync(new UserRemoved(name), caller: this);
        }
    }

    public async Task ClearAsync()
    {
        lock (_usersLock)
        {
            _usersByName.Clear();
        }
        await Cleared.RaiseAsync(caller: this);
    }

    /// <summary>
    /// The registry created the events, so it disposes them; the subscribe-only view cannot.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await UserCreated.DisposeAsync();
        await _userRemoved.DisposeAsync();
        await Cleared.DisposeAsync();
    }
}
