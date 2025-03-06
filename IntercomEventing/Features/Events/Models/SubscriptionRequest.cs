namespace IntercomEventing.Features.Events;

public record struct SubscriptionRequest<TEvent> where TEvent : GenericEvent<TEvent>
{
    public Func<TEvent, ValueTask> OnEventExecute { get; init; }
    public Func<Task>? OnUnsubscribe { get; init; }
    public Func<TEvent,Task>? OnSubscribe { get; init; }
    public Action<Exception>? ExceptionHandler { get; init; }

    public SubscriptionRequest(Func<TEvent, ValueTask> onEventExecute, Func<TEvent,Task>? onSubscribe = null, Func<Task>? onUnsubscribe = null, Action<Exception>? exceptionHandler = null)
    {
        OnEventExecute = onEventExecute;
        OnUnsubscribe = onUnsubscribe;
        OnSubscribe = onSubscribe;
        ExceptionHandler = exceptionHandler;
    }

    public static implicit operator SubscriptionRequest<TEvent>(Func<TEvent, ValueTask> onEventExecute)
    {
        return new(onEventExecute);
    }
    
}