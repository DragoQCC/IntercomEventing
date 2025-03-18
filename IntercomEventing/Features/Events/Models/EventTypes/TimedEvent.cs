namespace IntercomEventing.Features.Events;

public abstract record TimedEvent<TEvent> : GenericEvent<TEvent> where TEvent : TimedEvent<TEvent>
{
    private readonly PeriodicTimer _timer;
    private bool _autoReset;
    public TimeSpan Interval { get; } = TimeSpan.Zero;

    protected TimedEvent(TimeSpan interval, bool autoReset = true)
    {
        _timer = new PeriodicTimer(interval);
        Interval = interval;
        _autoReset = autoReset;
    }

    protected abstract TimedEventCall<TEvent> CreateTimedEventCall();

    public void Start() => Task.Run(StartAsync);

    private async Task StartAsync()
    {
        while (await _timer.WaitForNextTickAsync())
        {
            //by using dynamic it prevents the type returned by the child class from being converted back to the base type
            dynamic eventCall = CreateTimedEventCall();
            //Ok, with this being dynamic, as I know it is some form of TimedEventCall, I can set the interval
            eventCall.Interval = Interval;
            await RaiseEvent(eventCall);
            if (!_autoReset)
            {
                break;
            }
        }
        Stop();
    }

    public void Stop() => _timer.Dispose();
    
    public override async ValueTask DisposeAsync()
    {
        _timer.Dispose();
        await base.DisposeAsync();
    }
}

public abstract record TimedEventCall<TEvent>() : EventCall<TEvent>
where TEvent : TimedEvent<TEvent>
{
    public TimeSpan Interval { get; internal set; }

    /*protected TimedEventCall() : this(TimeSpan.Zero)
    {
    }*/
}