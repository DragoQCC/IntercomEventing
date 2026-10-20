using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Samples.BlazorServer.Features.Telemetry;

public partial class TelemetryPage
{
    private const int MaxAlerts = 6;

    private readonly List<string> _alerts = [];

    [Inject]
    private SensorHub Hub { get; set; } = default!;

    protected Sensor SelectedSensor { get; private set; } = default!;

    //bound to the sensor selector; changing it changes the cascaded event, which LiveReadingPanel resubscribes to
    protected string SelectedSensorName
    {
        get => SelectedSensor.Name;
        set => SelectedSensor = Hub.Sensors.First(sensor => sensor.Name == value);
    }

    protected IReadOnlyList<string> Alerts => _alerts;

    protected override void OnInitialized()
    {
        SelectedSensor = Hub.Sensors[0];
        foreach (var sensor in Hub.Sensors)
        {
            Events.Subscribe(sensor.Overheating, OnOverheating);
        }
    }

    protected void ToggleSampling()
    {
        if (SelectedSensor.IsRunning)
        {
            SelectedSensor.Stop();
        }
        else
        {
            SelectedSensor.Start();
        }
    }

    protected void ToggleCoolingFailure()
    {
        SelectedSensor.SetCoolingFailed(!SelectedSensor.IsCoolingFailed);
    }

    private void OnOverheating(ThresholdEventCall<int, SensorReading> call)
    {
        var reading = call.Payload;
        var alert = $"{call.Metadata.RaisedAt.ToLocalTime():HH:mm:ss} {reading.SensorName}: {call.CurrentValue} readings in a row " +
            $"above {Sensor.HotLimitCelsius} °C (latest {reading.Celsius} °C)";
        _alerts.Insert(0, alert);
        if (_alerts.Count > MaxAlerts)
        {
            _alerts.RemoveAt(_alerts.Count - 1);
        }
    }
}
