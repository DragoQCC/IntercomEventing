using HelpfulTypesAndExtensions;

namespace IntercomEventing.Features.Events;

/// <summary>
/// Configuration for the eventing system
/// </summary>
public class EventingConfiguration
{
    internal static bool IsSeq { get; set; }

    public EventingOptions EventingOptions { get; }
    internal static EventingOptions EventingOptionsInternal { get; private set; } = new();

    /// <summary>
    /// Configuration for the eventing system
    /// </summary>
    /// <param name="setOptions"> An optional action to configure the eventing options, such as the sync type and max number of concurrent handlers </param>
    /// <param name="exceptionHandler"> An optional action to handle exceptions that occur during event handling, exception handlers passed in during subscription will override this </param>
    public EventingConfiguration(Action<EventingOptions>? setOptions = null)
    {
        EventingOptions = new();
        setOptions?.Invoke(EventingOptions);
        EventingOptionsInternal = EventingOptions;
        IsSeq = EventingOptionsInternal.SyncType == EventingSyncType.Sequential;
    }
}
