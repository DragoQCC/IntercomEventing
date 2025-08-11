namespace IntercomEventing.Features.Events;

/// <summary>
/// Represents an event that is raised at a specified interval <br/>
/// The event will be optionally raised multiple times if auto reset is enabled  
/// </summary>
/// <typeparam name="TEvent"></typeparam>
public abstract record TimedEvent<TEvent> : GenericEvent<TEvent> where TEvent : TimedEvent<TEvent>
{
    private readonly PeriodicTimer _timer;
    private bool _autoReset;
    private TimeSpan _interval;

    /// <summary>
    /// Represents an event that is raised at a specified interval <br/>
    /// The event will be optionally raised multiple times if auto reset is enabled  <br/>
    /// </summary>
    /// <param name="interval"></param>
    /// <param name="autoReset"></param>
    protected TimedEvent(TimeSpan interval, bool autoReset = true)
    {
        _interval = interval;
        _timer = new PeriodicTimer(interval);
        _autoReset = autoReset;
    }

    ///<inheritdoc/>
    override abstract protected TimedEventCall<TEvent> CreateEventCall(params object[]? args);
    
    /// <summary>
    /// Starts the event to be raised at the specified interval <br/>
    /// </summary>
    public void Start() => Task.Run(StartAsync);

    
    private async Task StartAsync()
    {
        while (await _timer.WaitForNextTickAsync())
        {
            var eventCall = CreateEventCall();
            eventCall.Interval = _interval;
            await RaiseEvent(eventCall);
            if (!_autoReset)
            {
                break;
            }
        }
        Stop();
    }

    
    /// <summary>
    /// Stops the event from being raised <br/>
    /// </summary>
    public void Stop() => _timer.Dispose();
    
    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        _timer.Dispose();
        await base.DisposeAsync();
    }
}


/// <summary>
/// The event call for a timed event <br/>
/// Should be used to pass data to event handlers 
/// </summary>
/// <typeparam name="TEvent"></typeparam>
public abstract record TimedEventCall<TEvent> : EventCall<TEvent> where TEvent : TimedEvent<TEvent>
{
    public TimeSpan Interval { get; internal set; }
}