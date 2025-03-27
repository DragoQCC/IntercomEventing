namespace IntercomEventing.Benchmark.StateChangedEventExample;

public class ServerPowerService_EventProducer
{
    public ServerPowerChangedEvent ServerPowerChangedEvent { get; init; } = new();
    
    public async Task ChangeServerPowerState(Server server, ServerPower newState) => await ServerPowerChangedEvent.ChangeServerPowerState(server, newState);
}