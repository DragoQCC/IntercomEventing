namespace IntercomEventing.Features.Events;

public interface IEvent;

public interface IEvent<TEvent> : IEvent where TEvent : IEvent
{
    public EventMetadata Metadata { get; protected set; }
    public HashSet<Subscription<TEvent>> Subscribers { get; init; }


    public async Task RaiseEvent<TCaller>(TCaller? eventCaller = null) where TCaller : class?
    {
        if(Subscribers.Count == 0)
        {
            return;
        }
        Metadata = Metadata with {EventCaller = eventCaller, LastEventTime = DateTime.Now};
        await NotifySubscribers();
    }

    public async Task<Subscription<TEvent>> Subscribe(SubscriptionRequest<TEvent> subscriptionRequest)
    {
        Subscription<TEvent> subscription = new(this, subscriptionRequest);
        await AddSubscriber(subscription);
        return subscription;
    }

    public async Task<bool> Unsubscribe(Subscription<TEvent> subscription)
    {
        if(Subscribers.Contains(subscription))
        {
            await RemoveSubscriber(subscription);
            return true;
        }
        return false;
    }

    public async Task DeleteEvent()
    {
        foreach (Subscription<TEvent> subscription in Subscribers)
        {
            await subscription.DisposeAsync();
        }
        Subscribers.Clear();
    }

    public async Task AddSubscriber(Subscription<TEvent> subscription)
    {
        if(!Subscribers.Add(subscription))
        {
            return;
        }
        //TODO: Eval performance with long running or error prone event handlers
        await subscription.HandleSubscribe((TEvent)this);
    }

    public async Task RemoveSubscriber(Subscription<TEvent> subscription)
    {
        if(Subscribers.Contains(subscription))
        {
            Subscribers.Remove(subscription);
        }
        await subscription.DisposeAsync();
    }

    //Currently without any exception handler passed in the subscription request the event will throw an exception if an exception is thrown by a subscriber
    //this then blocks the execution of the rest of the subscribers
    public async Task NotifySubscribers()
    {
        // Fast path for single subscriber
        if (Subscribers.Count == 1)
        {
            var subscription = Subscribers.First();
            try
            {
                await subscription.HandleEventExecute((TEvent)this).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                subscription.TryHandleException(ex);
            }
            return;
        }

        if (EventingConfiguration.IsSync)
        {
            foreach (var subscription in Subscribers)
            {
                try
                {
                    await subscription.HandleEventExecute((TEvent)this).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    subscription.TryHandleException(ex);
                }
            }
        }
        else
        {
            var tasks = new ValueTask[Subscribers.Count];
            var i = 0;
            foreach (var subscription in Subscribers)
            {
                tasks[i++] = subscription.HandleEventExecute((TEvent)this);
            }
            await Task.WhenAll(tasks.Select(t => t.AsTask())).ConfigureAwait(false);
        }
    }

    public static Subscription<TEvent> operator +(IEvent<TEvent> @event, SubscriptionRequest<TEvent> subscriptionRequest)
        => @event.Subscribe(subscriptionRequest).Result;
}

public interface IEvent<TEvent,TEventArgs> : IEvent<TEvent>
where TEvent : IEvent
where TEventArgs : IEventArgs<TEvent>
{
    public TEventArgs EventArgs { get; set; }


    public async Task RaiseEvent(TEventArgs args, object? eventCaller = null)
    {
        if(Subscribers.Count == 0)
        {
            return;
        }
        Metadata = Metadata with {EventCaller = eventCaller, LastEventTime = DateTime.Now};
        EventArgs = args;
        await NotifySubscribers();
    }

    public static Subscription<TEvent> operator +(IEvent<TEvent,TEventArgs> @event, SubscriptionRequest<TEvent> subscriptionRequest)
        => @event.Subscribe(subscriptionRequest).Result;
}