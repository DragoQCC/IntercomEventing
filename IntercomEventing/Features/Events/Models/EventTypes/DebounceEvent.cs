namespace IntercomEventing.Features.Events;

/// <summary>
/// Represents an event that debounces calls to the event handler <br/>
/// The event will only be raised after the debounce interval has passed without any new calls to the event handler
/// </summary>
/// <typeparam name="TEvent"></typeparam>
public abstract record DebounceEvent<TEvent> : GenericEvent<TEvent>
    where TEvent : DebounceEvent<TEvent>
{
    private readonly TimeSpan _debounceInterval;
    private CancellationTokenSource? _debounceTokenSource;
    private TaskCompletionSource<bool>? _currentDebounce;
    private readonly Lock _debounceSyncLock = new();

    protected DebounceEvent(TimeSpan debounceInterval)
    {
        _debounceInterval = debounceInterval;
    }

    protected Task StartDebounce()
    {
        lock (_debounceSyncLock)
        {
            _debounceTokenSource?.Cancel();
            _debounceTokenSource = new CancellationTokenSource();
            _currentDebounce?.TrySetCanceled();
            _currentDebounce = new TaskCompletionSource<bool>();
            
            // Fire and forget
            _ = HandleDebounceAsync(_debounceTokenSource.Token, _currentDebounce);
        }
        return Task.CompletedTask;
    }

    private async Task HandleDebounceAsync(CancellationToken token, TaskCompletionSource<bool> completionSource)
    {
        try
        {
            await Task.Delay(_debounceInterval, token);
            
            // Only raise the event if this is still the current debounce task
            lock (_debounceSyncLock)
            {
                if (completionSource == _currentDebounce && !token.IsCancellationRequested)
                {
                    DebounceEventCall<TEvent> eventCall =  CreateEventCall();
                    eventCall.DebounceInterval = _debounceInterval;
                    _ = RaiseEvent(eventCall);
                    completionSource.TrySetResult(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when debouncing
            completionSource.TrySetCanceled();
        }
        catch (Exception ex)
        {
            completionSource.TrySetException(ex);
        }
    }

    override abstract protected DebounceEventCall<TEvent> CreateEventCall();

    public override async ValueTask DisposeAsync()
    {
        _debounceTokenSource?.Cancel();
        _debounceTokenSource?.Dispose();
        await base.DisposeAsync();
    }
}

/// <summary>
/// The event call for a debounce event <br/>
/// Should be used to pass data to event handlers
/// </summary>
/// <typeparam name="TEvent"></typeparam>
public record DebounceEventCall<TEvent> : EventCall<TEvent> where TEvent : DebounceEvent<TEvent>
{
    public TimeSpan DebounceInterval { get; internal set; }
}