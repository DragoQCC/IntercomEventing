namespace IntercomEventing.Features.Events;


public class EventingConfiguration
{
    public EventingOptions EventingOptions { get; init; }
    internal static EventingOptions EventingOptionsInternal { get; set; } = new();

    public EventingConfiguration(Action<EventingOptions>? setOptions = null)
    {
        EventingOptions = new();
        setOptions?.Invoke(EventingOptions);
        EventingOptionsInternal = EventingOptions;
    }
}