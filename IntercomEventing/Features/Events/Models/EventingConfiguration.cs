namespace IntercomEventing.Features.Events;


public class EventingConfiguration
{
    private static bool _isSync;
    internal static bool IsSync => _isSync;
    
    public EventingOptions EventingOptions { get; init; }
    internal static EventingOptions EventingOptionsInternal { get; set; } = new();

    public EventingConfiguration(Action<EventingOptions>? setOptions = null)
    {
        EventingOptions = new();
        setOptions?.Invoke(EventingOptions);
        EventingOptionsInternal = EventingOptions;
        _isSync = EventingOptionsInternal.SyncType == EventingSyncType.Sync;
    }
    
    
}
