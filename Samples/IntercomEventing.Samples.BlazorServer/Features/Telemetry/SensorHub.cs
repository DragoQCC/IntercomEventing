namespace IntercomEventing.Samples.BlazorServer.Features.Telemetry;

/// <summary>
/// The sensors of the server room. Registered as a singleton, so every tab watches the same sensors; they start sampling
/// when the hub is first used.
/// </summary>
public sealed class SensorHub : IAsyncDisposable
{
    public IReadOnlyList<Sensor> Sensors { get; } =
    [
        new Sensor("Rack A inlet", normalCelsius: 23),
        new Sensor("Rack B inlet", normalCelsius: 26),
    ];

    public SensorHub()
    {
        foreach (var sensor in Sensors)
        {
            sensor.Start();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sensor in Sensors)
        {
            await sensor.DisposeAsync();
        }
    }
}
