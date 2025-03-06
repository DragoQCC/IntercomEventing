/*using IntercomEventing.Features.Events;

namespace IntercomEventing;

public static class EventExtensions
{
    public static async ValueTask<Subscription<TEvent>> Subscribe<TEvent>(this TEvent subscribedEvent,
        Func<TEvent,ValueTask> onEventExecute, Func<TEvent,Task>? onSubscribe = null,
        Func<Task>? onUnsubscribe = null, Action<Exception>? exceptionHandler = null)
    where TEvent : IEvent<TEvent>
    {
        SubscriptionRequest<TEvent> subscriptionRequest = new(onEventExecute, onSubscribe, onUnsubscribe,exceptionHandler);
        return await subscribedEvent.Subscribe(subscriptionRequest);
    }

    public static async Task<Subscription<TEvent>> Subscribe<TEvent,TEventArgs>(this TEvent subscribedEvent,
        Func<TEvent,ValueTask> onEventExecute, Func<TEvent,Task>? onSubscribe = null,
        Func<Task>? onUnsubscribe = null, Action<Exception>? exceptionHandler = null)
    where TEvent : IEvent<TEvent,TEventArgs>
    where TEventArgs : IEventArgs<TEvent>
    {
        SubscriptionRequest<TEvent> subscriptionRequest = new(onEventExecute, onSubscribe, onUnsubscribe,exceptionHandler);
        return await subscribedEvent.Subscribe(subscriptionRequest);
    }

    public static async Task<Subscription<TEvent>> Subscribe<TEvent>(this TEvent subscribedEvent, SubscriptionRequest<TEvent> subscriptionRequest)
    where TEvent : IEvent<TEvent>
        => await subscribedEvent.Subscribe(subscriptionRequest);

    public static async Task<bool> Unsubscribe<TEvent>(this TEvent subscribedEvent, Subscription<TEvent> subscription)
    where TEvent : IEvent<TEvent>
    {
        return await subscribedEvent.Unsubscribe(subscription);
    }

    public static async Task DeleteEvent<TEvent>(this TEvent @event)
    where TEvent : IEvent<TEvent>
        => await @event.DeleteEvent();

    public static async Task RaiseEvent<TEvent,TCaller>(this TEvent @event, TCaller? eventCaller = null)
    where TEvent : IEvent<TEvent> 
    where TCaller : class?
        => await @event.RaiseEvent(eventCaller);
    
    public static async Task RaiseEvent<TEvent>(this TEvent @event, object? eventCaller = null)
    where TEvent : IEvent<TEvent> 
        => await @event.RaiseEvent(eventCaller);

    public static async Task RaiseEvent<TEvent,TEventArgs>(this TEvent @event, TEventArgs args, object? eventCaller = null)
    where TEvent : IEvent<TEvent,TEventArgs>
    where TEventArgs : IEventArgs<TEvent>
        => await @event.RaiseEvent(args, eventCaller);
    
    public static async Task RaiseEvent<TEvent,TEventArgs,TCaller>(this TEvent @event, TEventArgs args, TCaller? eventCaller = null)
    where TEvent : IEvent<TEvent,TEventArgs>
    where TCaller : class?
    where TEventArgs : IEventArgs<TEvent>
        => await @event.RaiseEvent(args, eventCaller);
}*/