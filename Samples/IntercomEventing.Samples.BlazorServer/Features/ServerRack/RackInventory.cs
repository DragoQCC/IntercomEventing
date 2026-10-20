namespace IntercomEventing.Samples.BlazorServer.Features.ServerRack;

/// <summary>
/// The servers of the rack. Registered as a singleton, so every browser tab sees the same servers and the same events.
/// </summary>
public sealed class RackInventory : IAsyncDisposable
{
    public IReadOnlyList<RackServer> Servers { get; } =
    [
        new RackServer("web-01"),
        new RackServer("api-01"),
        new RackServer("db-01"),
    ];

    public async ValueTask DisposeAsync()
    {
        foreach (var server in Servers)
        {
            await server.DisposeAsync();
        }
    }
}
