using HelpfulTypesAndExtensions;
using IntercomEventing.Features.Events;

namespace IntercomEventing.Benchmark.StateChangedEventExample;

public record ServerPower : IEnumeration<ServerPower>
{
    public static Enumeration<ServerPower> ServerPowerStates { get; internal set; } = new();
    
    /// <inheritdoc />
    public int Value { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    public ServerPower(int id, string name)
    {
        Value = id;
        DisplayName = name;
    }
    
    public static readonly ServerPower Off = new(0, nameof(Off));
    public static readonly ServerPower On = new(1, nameof(On));
    public static readonly ServerPower Restarting = new(2, nameof(Restarting));
    public static readonly ServerPower Starting = new(3, nameof(Starting));
    public static readonly ServerPower Stopping = new(4, nameof(Stopping));
}

public record ServerPowerChangedEvent() : StateChangeEvent<ServerPowerChangedEvent, ServerPower>(ServerPower.Off)
{
    private Server _server;
    
    public async Task ChangeServerPowerState(Server server, ServerPower newState)
    {
        _server = server;
        CurrentState = newState;
    }

    /// <inheritdoc />
    override protected StateChangeEventCall<ServerPowerChangedEvent, ServerPower> CreateEventCall() => new ServerPowerChangedEventCall(_server);
}

public record ServerPowerChangedEventCall(Server Server) : StateChangeEventCall<ServerPowerChangedEvent, ServerPower>;