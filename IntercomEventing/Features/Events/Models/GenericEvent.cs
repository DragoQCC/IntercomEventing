

namespace IntercomEventing.Features.Events;

public abstract record GenericEvent<TEvent> : IAsyncDisposable where TEvent : GenericEvent<TEvent>
{
    public EventMetadata Metadata { get; protected set; } = new();
    public HashSet<Subscription<TEvent>> Subscribers { get; init; } = new();

    public async Task RaiseEvent<TCaller>(TCaller? eventCaller = null) where TCaller : class?
    {
        if(Subscribers.Count == 0) return;
        
        Metadata = Metadata with 
        { 
            EventCaller = eventCaller, 
            LastEventTime = DateTime.UtcNow 
        };
        await NotifySubscribers();
    }

    public async ValueTask<Subscription<TEvent>> Subscribe(SubscriptionRequest<TEvent> subscriptionRequest)
    {
        var subscription = new Subscription<TEvent>((TEvent)this, subscriptionRequest);
        await AddSubscriber(subscription);
        return subscription;
    }
    
    public async ValueTask<Subscription<TEvent>> Subscribe(Func<TEvent, ValueTask> onEventExecute, Func<TEvent,Task>? onSubscribe = null, Func<Task>? onUnsubscribe = null, Action<Exception>? exceptionHandler = null)
    {
        var subscription = new Subscription<TEvent>((TEvent)this, onEventExecute, onSubscribe, onUnsubscribe, exceptionHandler);
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
        foreach (var subscription in Subscribers)
        {
            await subscription.DisposeAsync();
        }
        Subscribers.Clear();
    }

    protected async ValueTask AddSubscriber(Subscription<TEvent> subscription)
    {
        if (!EventingConfiguration.EventingOptionsInternal.AllowMultipleSubscribers && Subscribers.Count > 0)
        {
            throw new InvalidOperationException("Multiple subscribers are not allowed for this event.");
        }

        if (!Subscribers.Add(subscription)) return;
        await subscription.HandleSubscribe();
    }

    protected async Task RemoveSubscriber(Subscription<TEvent> subscription)
    {
        if(Subscribers.Contains(subscription))
        {
            Subscribers.Remove(subscription);
        }
        await subscription.DisposeAsync();
    }

    protected async Task NotifySubscribers()
    {
        var options = EventingConfiguration.EventingOptionsInternal;
        var timeout = options.StartNextEventHandlerAfter;

        // Fast path for single subscriber
        if (Subscribers.Count == 1)
        {
            var subscription = Subscribers.First();
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                await ExecuteWithTimeout(subscription, cts.Token);
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
                    using var cts = new CancellationTokenSource(timeout);
                    await ExecuteWithTimeout(subscription, cts.Token);
                }
                catch (Exception ex)
                {
                    subscription.TryHandleException(ex);
                }
            }
        }
        else
        {
            var tasks = new List<Task>(Subscribers.Count);
            foreach (var subscription in Subscribers)
            {
                var task = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(timeout);
                        await ExecuteWithTimeout(subscription, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        subscription.TryHandleException(ex);
                    }
                });
                tasks.Add(task);

                if (tasks.Count >= options.MaxThreadsPerEvent)
                {
                    await Task.WhenAny(tasks);
                    tasks.RemoveAll(t => t.IsCompleted);
                }
            }

            await Task.WhenAll(tasks);
        }
    }

    private async Task ExecuteWithTimeout(Subscription<TEvent> subscription, CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, subscription.SubCancelToken);
            var timeoutTask = Task.Delay(EventingConfiguration.EventingOptionsInternal.StartNextEventHandlerAfter, linkedCts.Token);
            var executionTask = subscription.HandleEventExecute((TEvent)this).AsTask();

            var completedTask = await Task.WhenAny(executionTask, timeoutTask);
            if (completedTask == timeoutTask)
            {
                // Don't cancel the execution, just continue execution in the background
                _ = executionTask.ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        subscription.TryHandleException(t.Exception.InnerException ?? t.Exception);
                    }
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            else
            {
                // If the execution completed before timeout, await it to propagate any exceptions
                await executionTask;
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Event handler was cancelled after {EventingConfiguration.EventingOptionsInternal.StartNextEventHandlerAfter.TotalMilliseconds}ms");
        }
        catch (Exception ex)
        {
            subscription.TryHandleException(ex);
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        await DeleteEvent();
    }
}

public abstract record GenericEvent<TEvent, TEventArgs> : GenericEvent<TEvent> where TEvent : GenericEvent<TEvent, TEventArgs> where TEventArgs : IEventArgs<TEvent>
{
    public TEventArgs EventArgs { get; set; }

    public async Task RaiseEvent(TEventArgs args, object? eventCaller = null)
    {
        if(Subscribers.Count == 0) return;

        Metadata = Metadata with 
        { 
            EventCaller = eventCaller, 
            LastEventTime = DateTime.UtcNow 
        };
        EventArgs = args;
        await NotifySubscribers();
    }
}