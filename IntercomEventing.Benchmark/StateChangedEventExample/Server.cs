namespace IntercomEventing.Benchmark.StateChangedEventExample;

public class Server
{
    public ServerPower PowerState { get; set; } = ServerPower.Off;
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
}