namespace IntercomEventing.Features.Events;

/// <summary>
/// Options for configuring the eventing system
/// </summary>
public class EventingOptions
{
    /// <summary>
    /// Controls whether event handlers are executed sequentially or in parallel on a per event call basis
    /// </summary>
    public EventingSyncType SyncType { get; set; } = EventingSyncType.Parallel;

    /// <summary>
    /// The maximum number of event handlers that can be executed at the same time on a per event call basis <br/>
    /// Default values are 100 for parallel and 1 for sequential
    /// </summary>
    public int MaxNumberOfConcurrentHandlers { get; set; }

    /// <summary>
    /// If true then more than one subscriber can subscribe to the same event,
    /// otherwise only one subscriber can subscribe to the event
    /// </summary>
    public bool AllowMultipleSubscribers { get; set; } = true;

    /// <summary>
    /// The time to wait before starting the next event handler <br/>
    /// Default value is 1 second <br/>
    /// The current event handler will continue to run within a Task <see cref="Task{TResult}.Run(Func{Task})"/> in the background
    /// </summary>
    public TimeSpan StartNextEventHandlerAfter { get; set; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// The default exception handler to use for all events <br/>
    /// If an exception handler is not provided when subscribing to an event, this handler will be used <br/>
    /// If no default exception handler is provided, and one is not provided when subscribing to an event, exceptions will be suppressed and not propagated to the caller 
    /// </summary>
    public Action<Exception>? DefaultExceptionHandler { get; set; }


    public EventingOptions()
    {
        if(MaxNumberOfConcurrentHandlers == 0)
        {
            MaxNumberOfConcurrentHandlers = SetMaxConcurrentEventHandlers(this);
        }
    }

    internal static int SetMaxConcurrentEventHandlers(EventingOptions options) => options.SyncType == EventingSyncType.Parallel ? 100 : 1;
}