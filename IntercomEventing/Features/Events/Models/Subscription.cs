namespace IntercomEventing.Features.Events;


public sealed record Subscription<TEvent> : ISubscription
where TEvent : IEvent
{
    private Func<TEvent, ValueTask> OnEventExecute { get; set; }
    private Func<Task>? OnUnsubscribe { get; init; }
    private Func<TEvent,Task>? OnSubscribe { get; init; }
    private Action<Exception>? ExceptionHandler { get; init; }
    public CancellationToken SubCancelToken { get; init; }
    internal CancellationTokenSource SubCancelTokenSource { get; init; }

    private bool _isDisposed;


    public Subscription(Func<TEvent, ValueTask> onEventExecute, Func<TEvent,Task>? onSubscribe = null, Func<Task>? onUnsubscribe = null, Action<Exception>? exceptionHandler = null)
    {
        OnEventExecute = onEventExecute;
        OnUnsubscribe = onUnsubscribe;
        OnSubscribe = onSubscribe;
        ExceptionHandler = exceptionHandler;
        SubCancelTokenSource = new();
        SubCancelToken = SubCancelTokenSource.Token;
    }

    public Subscription(SubscriptionRequest<TEvent> subscriptionRequest)
    {
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

    internal async Task HandleSubscribe(TEvent @event)
    {
        if (OnSubscribe is not null)
        {
            await OnSubscribe.Invoke(@event);
        }
    }

    internal async Task HandleUnsubscribe()
    {
        if (OnUnsubscribe is not null)
        {
            await OnUnsubscribe.Invoke();
        }
        else
        {
            await Task.CompletedTask;
        }
    }

    public async ValueTask HandleEventExecute(TEvent @event)
    {
        if(SubCancelToken.IsCancellationRequested)
        {
            return;
        }
        await OnEventExecute(@event);
    }

    internal void TryHandleException(Exception ex) => ExceptionHandler?.Invoke(ex);

    /// <inheritdoc />
    public void Dispose()
    {
        Console.WriteLine("Disposing subscription");
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        HandleUnsubscribe().Wait(SubCancelToken);
        SubCancelTokenSource.Cancel();
        SubCancelTokenSource.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Console.WriteLine("Disposing subscription async");
        if(_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        await HandleUnsubscribe();
        await SubCancelTokenSource.CancelAsync();
        SubCancelTokenSource.Dispose();
    }
}