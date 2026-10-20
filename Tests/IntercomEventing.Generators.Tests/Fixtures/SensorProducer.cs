namespace IntercomEventing.Generators.Tests.Fixtures;

/// <summary>
/// A struct producer: copies share the event object, so a subscription made through a copy is seen by every copy.
/// </summary>
public readonly partial struct SensorProducer
{
    [IntercomEvent]
    private readonly IntercomEvent<double> _readingTaken;

    public SensorProducer()
    {
        _readingTaken = new IntercomEvent<double>();
    }

    public Task TakeReadingAsync(double reading)
    {
        return _readingTaken.RaiseAsync(reading);
    }
}
