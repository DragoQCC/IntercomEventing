namespace IntercomEventing.Tests.TestSupport;

//payloads are plain records that know nothing about IntercomEventing, as in the documented examples

public sealed record UserCreated(string Name, string Email);

public sealed record OrderPlaced(int OrderNumber, string Customer, decimal Total);

public sealed record LoginAttempt(string UserName, string IpAddress);

public sealed record ServerInfo(string Name, string IpAddress);

public sealed record SearchQuery(string Text);

public sealed record SessionUser(string Name);

/// <summary>
/// A payload with its own <c>Metadata</c> member, the shape of AvionRelay's call records (defect A11).
/// </summary>
public sealed record ClientConnected(string ClientName, IReadOnlyDictionary<string, string> Metadata);

public enum PowerState
{
    Off,
    Starting,
    On,
    Restarting,
}

/// <summary>
/// A named event type, the documented way to keep an existing type name: handlers still receive
/// <c>EventCall&lt;ClientConnected&gt;</c>.
/// </summary>
public sealed class ClientConnectedEvent : IntercomEvent<ClientConnected>
{
    public ClientConnectedEvent(EventingOptions? options = null)
        : base(options)
    {
    }
}
