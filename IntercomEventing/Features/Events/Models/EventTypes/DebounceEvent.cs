using HelpfulTypesAndExtensions;

namespace IntercomEventing.Features.Events;

public abstract record DebounceEvent<TEvent> : GenericEvent<TEvent>
    where TEvent : DebounceEvent<TEvent>
{
    private readonly TimeSpan _debounceInterval;
    //private DateTime _lastEventTime = DateTime.MinValue;
    //private readonly SemaphoreSlim _debounceLock = new(1);
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
                    //_lastEventTime = DateTime.UtcNow;
                    // Fire and forget the event raising to prevent deadlocks
                    _ = RaiseEvent(CreateDebounceEventCall(_debounceInterval));
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

    protected virtual DebounceEventCall<TEvent> CreateDebounceEventCall(TimeSpan debounceInterval)
    {
        return new DebounceEventCall<TEvent>(debounceInterval);
    }

    public override async ValueTask DisposeAsync()
    {
        _debounceTokenSource?.Cancel();
        _debounceTokenSource?.Dispose();
        //_debounceLock.Dispose();
        await base.DisposeAsync();
    }
}

public record DebounceEventCall<TEvent>(TimeSpan DebounceInterval) : EventCall<TEvent> where TEvent : DebounceEvent<TEvent>;