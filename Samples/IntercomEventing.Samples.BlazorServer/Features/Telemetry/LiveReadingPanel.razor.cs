using System.Globalization;
using IntercomEventing.Blazor;
using IntercomEventing.Samples.BlazorServer.EventKinds.Throttle;
using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Samples.BlazorServer.Features.Telemetry;

/// <summary>
/// Shows the readings of whichever sensor event is cascaded to it.
/// </summary>
/// <remarks>
/// It uses <see cref="ComponentEventSubscriptions"/> directly, the way a component that already has another base class
/// would, instead of deriving from <see cref="IntercomComponentBase"/>. The cascaded event can change between renders, so
/// <see cref="ComponentEventSubscriptions.Resubscribe{TCall}(EventSubscription{TCall}?, IIntercomEvent{TCall}?, Action{TCall}, Action{Exception, TCall}?, bool)"/>
/// in <see cref="OnParametersSet"/> keeps exactly one subscription, on the current event.
/// </remarks>
public partial class LiveReadingPanel : IAsyncDisposable
{
    private const int HistoryLength = 40;
    private const double ChartMinimumCelsius = 15;
    private const double ChartMaximumCelsius = 40;

    private readonly ComponentEventSubscriptions _events;
    private readonly Queue<SensorReading> _history = new();
    private EventSubscription<ThrottleEventCall<SensorReading>>? _readingSubscription;
    private IIntercomEvent<ThrottleEventCall<SensorReading>>? _displayedReadings;

    //the subscribe-only view type: the cascaded ThrottleEvent<SensorReading> matches it because it is assignable to it
    [CascadingParameter]
    public IIntercomEvent<ThrottleEventCall<SensorReading>>? Readings { get; set; }

    protected SensorReading? Latest { get; private set; }

    protected int DroppedOfferCount { get; private set; }

    protected int UpdatesReceived { get; private set; }

    protected IEnumerable<SensorReading> History => _history;

    public LiveReadingPanel()
    {
        //InvokeAsync, StateHasChanged, DispatchExceptionAsync and RendererInfo are protected, so the component hands them over
        _events = new ComponentEventSubscriptions(InvokeAsync, StateHasChanged, DispatchExceptionAsync, () => RendererInfo);
    }

    public ValueTask DisposeAsync()
    {
        return _events.DisposeAsync();
    }

    protected override void OnParametersSet()
    {
        var isOtherSensor = !ReferenceEquals(_displayedReadings, Readings);
        if (isOtherSensor)
        {
            _displayedReadings = Readings;
            ClearDisplay();
        }
        //subscribes to the new event before removing the old subscription; returns the current one when nothing changed
        _readingSubscription = _events.Resubscribe(_readingSubscription, Readings, OnReadingSampled);
    }

    protected static string BarHeightPercent(SensorReading reading)
    {
        var clampedCelsius = Math.Clamp(reading.Celsius, ChartMinimumCelsius, ChartMaximumCelsius);
        var percent = (clampedCelsius - ChartMinimumCelsius) / (ChartMaximumCelsius - ChartMinimumCelsius) * 100;
        return percent.ToString("0", CultureInfo.InvariantCulture);
    }

    private void OnReadingSampled(ThrottleEventCall<SensorReading> call)
    {
        Latest = call.Payload;
        DroppedOfferCount = call.DroppedOfferCount;
        UpdatesReceived++;
        _history.Enqueue(call.Payload);
        if (_history.Count > HistoryLength)
        {
            _history.Dequeue();
        }
    }

    private void ClearDisplay()
    {
        Latest = null;
        DroppedOfferCount = 0;
        UpdatesReceived = 0;
        _history.Clear();
    }
}
