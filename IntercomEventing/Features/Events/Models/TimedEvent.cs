using System.Timers;

namespace IntercomEventing.Features.Events;

public abstract record TimedEvent<TEvent> : GenericEvent<TEvent> where TEvent : TimedEvent<TEvent>
{
    private readonly System.Timers.Timer _timer;
    
    public TimeSpan Interval 
    { 
        get => TimeSpan.FromMilliseconds(_timer.Interval);
        set => _timer.Interval = value.TotalMilliseconds;
    }
    
    public bool AutoReset
    {
        get => _timer.AutoReset;
        set => _timer.AutoReset = value;
    }

    protected TimedEvent(TimeSpan interval, bool autoReset = true)
    {
        _timer = new System.Timers.Timer(interval.TotalMilliseconds) 
        { 
            AutoReset = autoReset 
        };
        _timer.Elapsed += async (_, _) => await RaiseEvent<TimedEvent<TEvent>>(this);
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
    
    public override async ValueTask DisposeAsync()
    {
        _timer.Stop();
        _timer.Dispose();
        await base.DisposeAsync();
    }
}