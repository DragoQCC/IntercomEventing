namespace IntercomEventing.Features.Events;


public sealed record Subscription<TEvent> : ISubscription
where TEvent : GenericEvent<TEvent>
{
    private Func<TEvent, ValueTask> OnEventExecute { get; set; }
    private Func<Task>? OnUnsubscribe { get; init; }
    private Func<TEvent,Task>? OnSubscribe { get; init; }
    private Action<Exception>? ExceptionHandler { get; init; }
    public CancellationToken SubCancelToken { get; init; }
    internal CancellationTokenSource SubCancelTokenSource { get; init; }
    
    // Add reference to the event
    private readonly TEvent _subscribedEvent;
    private bool _isDisposed;

    public Subscription(TEvent subscribedEvent, Func<TEvent, ValueTask> onEventExecute, Func<TEvent,Task>? onSubscribe = null, 
        Func<Task>? onUnsubscribe = null, Action<Exception>? exceptionHandler = null)
    {
        _subscribedEvent = subscribedEvent;
        OnEventExecute = onEventExecute;
        OnUnsubscribe = onUnsubscribe;
        OnSubscribe = onSubscribe;
        ExceptionHandler = exceptionHandler;
        SubCancelTokenSource = new();
        SubCancelToken = SubCancelTokenSource.Token;
    }

    public Subscription(TEvent subscribedEvent, SubscriptionRequest<TEvent> subscriptionRequest)
    {
        _subscribedEvent = subscribedEvent;
        OnEventExecute = subscriptionRequest.OnEventExecute;
        OnUnsubscribe = subscriptionRequest.OnUnsubscribe;
        OnSubscribe = subscriptionRequest.OnSubscribe;
        ExceptionHandler = subscriptionRequest.ExceptionHandler;
        SubCancelTokenSource = new();
        SubCancelToken = SubCancelTokenSource.Token;
    }

    ~Subscription()
    {
        Dispose();
    }

    internal async ValueTask HandleSubscribe()
    {
        if (OnSubscribe is not null)
        {
            await OnSubscribe.Invoke(_subscribedEvent);
        }
    }

    internal async Task HandleUnsubscribe()
    {
        if (OnUnsubscribe is not null)
        {
            await OnUnsubscribe.Invoke();
        }
        await _subscribedEvent.Unsubscribe(this);
    }

    public ValueTask HandleEventExecute(TEvent @event)
    {
        if(SubCancelToken.IsCancellationRequested)
        {
            return ValueTask.CompletedTask;
        }
        return OnEventExecute(@event);
    }

    internal void TryHandleException(Exception ex) => ExceptionHandler?.Invoke(ex);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        
        // Unsubscribe from the event
        HandleUnsubscribe().Wait(SubCancelToken);
        SubCancelTokenSource.Cancel();
        SubCancelTokenSource.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if(_isDisposed)
        {
            return;
        }
        _isDisposed = true;

        // Unsubscribe from the event
        await HandleUnsubscribe();
        await SubCancelTokenSource.CancelAsync();
        SubCancelTokenSource.Dispose();
    }
}