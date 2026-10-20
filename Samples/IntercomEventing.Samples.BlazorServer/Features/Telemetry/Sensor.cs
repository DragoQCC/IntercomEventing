using IntercomEventing.Samples.BlazorServer.EventKinds.Throttle;

namespace IntercomEventing.Samples.BlazorServer.Features.Telemetry;

public sealed record SensorReading(string SensorName, double Celsius, bool IsHot, DateTimeOffset MeasuredAt);

/// <summary>
/// A simulated temperature sensor that combines three event kinds:
/// <list type="bullet">
/// <item><description>a private <see cref="TimedEvent"/> takes a reading every <see cref="SampleInterval"/>;</description></item>
/// <item><description><see cref="ReadingSampled"/>, the custom <see cref="ThrottleEvent{TPayload}"/>, passes at most one reading
/// per <see cref="UiUpdateInterval"/> to the UI;</description></item>
/// <item><description><see cref="Overheating"/>, a <see cref="ThresholdEvent{TValue, TPayload}"/>, counts consecutive hot readings
/// and raises once when the count reaches <see cref="HotReadingsBeforeAlert"/>.</description></item>
/// </list>
/// Subscribers get the subscribe-only views, so only the sensor can raise.
/// </summary>
public sealed class Sensor : IAsyncDisposable
{
    public const double HotLimitCelsius = 30;

    //ten hot readings in a row: about one second of overheating at the sample interval
    private const int HotReadingsBeforeAlert = 10;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan UiUpdateInterval = TimeSpan.FromMilliseconds(500);

    private readonly double _normalCelsius;
    private readonly TimedEvent _sampleClock;
    private readonly ThrottleEvent<SensorReading> _readingSampled;
    private readonly ThresholdEvent<int, SensorReading> _overheating;

    //only the sample handler writes it, and ticks of one TimedEvent schedule never overlap, so it needs no lock
    private double _currentCelsius;
    private volatile bool _isCoolingFailed;
    private volatile SensorReading? _latestReading;

    public string Name { get; }

    public IIntercomEvent<ThrottleEventCall<SensorReading>> ReadingSampled => _readingSampled;

    public IIntercomEvent<ThresholdEventCall<int, SensorReading>> Overheating => _overheating;

    public bool IsRunning => _sampleClock.IsRunning;

    public bool IsCoolingFailed => _isCoolingFailed;

    public SensorReading? LatestReading => _latestReading;

    public Sensor(string name, double normalCelsius)
    {
        Name = name;
        _normalCelsius = normalCelsius;
        _currentCelsius = normalCelsius;
        _sampleClock = new TimedEvent(SampleInterval) { Name = $"{name} sample clock" };
        _readingSampled = new ThrottleEvent<SensorReading>(UiUpdateInterval) { Name = $"{name} readings" };
        _overheating = new ThresholdEvent<int, SensorReading>(HotReadingsBeforeAlert, ThresholdRaiseMode.OnlyWhenCrossed)
        {
            Name = $"{name} overheating",
        };
        //the sensor is the only subscriber of its own clock; the clock's disposal removes the subscription
        _sampleClock.Subscribe(OnSampleDueAsync);
    }

    public void Start()
    {
        _sampleClock.Start(caller: this);
    }

    public void Stop()
    {
        _sampleClock.Stop();
    }

    public void SetCoolingFailed(bool isCoolingFailed)
    {
        _isCoolingFailed = isCoolingFailed;
    }

    public async ValueTask DisposeAsync()
    {
        //the clock first, so no tick offers a reading to an event that is already disposed
        await _sampleClock.DisposeAsync();
        await _readingSampled.DisposeAsync();
        await _overheating.DisposeAsync();
    }

    private async Task OnSampleDueAsync(TimedEventCall<NoPayload> call)
    {
        var reading = TakeReading(call.Metadata.RaisedAt);
        _latestReading = reading;
        //never waits: the throttle raises in the background, at most once per UiUpdateInterval
        _readingSampled.Offer(reading, caller: this);
        if (reading.IsHot)
        {
            //OnlyWhenCrossed raises once, on the reading that makes the streak long enough, not on every hot reading after it
            await _overheating.IncrementAsync(1, reading, caller: this, call.CancellationToken);
        }
        else
        {
            //a normal reading ends the streak without raising, so the next streak can raise again
            _overheating.Reset();
        }
    }

    private SensorReading TakeReading(DateTimeOffset measuredAt)
    {
        //a random walk towards a target temperature; a cooling failure moves the target above the hot limit
        var targetCelsius = _isCoolingFailed ? HotLimitCelsius + 6 : _normalCelsius;
        var drift = (targetCelsius - _currentCelsius) * 0.08;
        var noise = (Random.Shared.NextDouble() - 0.5) * 0.6;
        _currentCelsius += drift + noise;

        var celsius = Math.Round(_currentCelsius, 1);
        var isHot = celsius > HotLimitCelsius;
        return new SensorReading(Name, celsius, isHot, measuredAt);
    }
}
