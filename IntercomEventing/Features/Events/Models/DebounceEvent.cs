namespace IntercomEventing.Features.Events;

public abstract record DebounceEvent<TEvent> : GenericEvent<TEvent>
    where TEvent : DebounceEvent<TEvent>
{
    private readonly TimeSpan _debounceInterval;
    private DateTime _lastEventTime = DateTime.MinValue;
    private readonly SemaphoreSlim _debounceLock = new(1);

    protected DebounceEvent(TimeSpan debounceInterval)
    {
        _debounceInterval = debounceInterval;
    }

    public async Task TryRaiseEvent()
    {
        await _debounceLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastEventTime >= _debounceInterval)
            {
                _lastEventTime = now;
                await RaiseEvent<DebounceEvent<TEvent>>(this);
            }
        }
        finally
        {
            _debounceLock.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _debounceLock.Dispose();
        await base.DisposeAsync();
    }
}