namespace IntercomEventing.Benchmark.StateChangedEventExample;

public class ServerPowerState_EventSubscriber
{
    public async Task HandleServerPowerChangedEventAsync(ServerPowerChangedEventCall eventCall)
    {
        string callTime = eventCall.Metadata.LastEventTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
        Console.WriteLine($"Server {eventCall.Server.Name} power changed from {eventCall.OldState} to {eventCall.NewState} at {callTime}");
        await Task.Delay(100);
    }
    
}