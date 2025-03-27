namespace IntercomEventing.Features.Events;


/// <summary>
/// Represents a subscription to an event <br/>
/// A subscription defines how the event should be handled, including initial actions during subscription, and any cleanup actions during unsubscription <br/>
/// Subscriptions are valid for the life of the event (i.e. until the event is disposed), or until the subscription is canceled earlier
/// </summary>
/// <typeparam name="TEvent">The type of event that is being subscribed to</typeparam>
public record Subscription<TEvent> where TEvent : GenericEvent<TEvent>
{
    private Func<EventCall<TEvent>, Task> OnEventExecute { get; }
    private Func<Task>? OnUnsubscribe { get; }
    private Func<TEvent,Task>? OnSubscribe { get; }
    private Action<Exception>? ExceptionHandler { get; }
    public CancellationToken SubCancelToken { get;  }
    internal CancellationTokenSource SubCancelTokenSource { get;  }
    
    // Add reference to the event
    private readonly TEvent _subscribedEvent;
    private bool _isDisposed;

    public Subscription(TEvent subscribedEvent, Func<EventCall<TEvent>, Task> onEventExecute, Func<TEvent,Task>? onSubscribe = null, Func<Task>? onUnsubscribe = null, Action<Exception>? exceptionHandler = null)
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

    internal Task HandleEventExecute(EventCall<TEvent> eventCall)
    {
        if(SubCancelToken.IsCancellationRequested)
        {
            return Task.FromCanceled(SubCancelToken);
        }
        try
        {
            return OnEventExecute(eventCall);
        }
        catch (Exception e)
        {
            TryHandleException(e);
            return Task.CompletedTask;
        }
    }

    internal void TryHandleException(Exception ex) 
    {
        if(ExceptionHandler is not null)
        {
            ExceptionHandler?.Invoke(ex);
            return;
        }
        EventingConfiguration.EventingOptionsInternal.DefaultExceptionHandler?.Invoke(ex);
    }

    /// <summary>
    /// Unsubscribes from the event, and cleans up any resources used by the subscription (ex. cancellation tokens)
    /// </summary>
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

    
    /// <summary>
    /// Unsubscribes from the event, and cleans up any resources used by the subscription (ex. cancellation tokens)
    /// </summary>
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

public record Subscription<TEvent, TEventCall> : Subscription<TEvent> where TEvent : GenericEvent<TEvent> where TEventCall : EventCall<TEvent>
{
    public Subscription(TEvent subscribedEvent, Func<TEventCall, Task> onEventExecute, Func<TEvent,Task>? onSubscribe = null, Func<Task>? onUnsubscribe = null, Action<Exception>? exceptionHandler = null)
        : base(subscribedEvent, eventCall => onEventExecute((TEventCall)eventCall), onSubscribe, onUnsubscribe, exceptionHandler)
    {
    }
}