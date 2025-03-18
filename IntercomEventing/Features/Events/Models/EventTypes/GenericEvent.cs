using System.Runtime.CompilerServices;
using HelpfulTypesAndExtensions;
using IntercomEventing.Models;

namespace IntercomEventing.Features.Events;

public abstract record GenericEvent<TEvent> : IAsyncDisposable where TEvent : GenericEvent<TEvent>
{
    private static readonly TimerCallback s_timerCallback = state => ((TaskCompletionSource<bool>)state!).TrySetResult(true);
    private static readonly ObjectPool<Timer> s_timerPool = new(() => new Timer(s_timerCallback, null, Timeout.Infinite, Timeout.Infinite));
    private static readonly ObjectPool<TaskCompletionSource<bool>> s_tcsPool = new(() => new TaskCompletionSource<bool>());
    
    public Guid Id { get; init; } = Guid.CreateVersion7(DateTime.UtcNow);
    public HashSet<Subscription<TEvent>> Subscribers { get; init; } = new();

    public async Task RaiseEvent(object? eventCaller = null)
    {
        if(Subscribers.Count == 0)
        {
            return;
        }
        EventCall<TEvent> eventCall = new();
        eventCall.Metadata.EventCaller = eventCaller;
        eventCall.Metadata.EventId = Id;
        await NotifySubscribers(eventCall);
    }
    
    public async Task RaiseEvent<TEventCall>(TEventCall eventCall, object? eventCaller = null) where TEventCall : EventCall<TEvent>
    {
        if(Subscribers.Count == 0)
        {
            return;
        }
        eventCall.Metadata.EventCaller = eventCaller;
        eventCall.Metadata.EventId = Id;
        await NotifySubscribers(eventCall);
    }
    
    
    //TODO: Eval the logic of keeping this method, event handlers should want to always take a TEventCall type unless im not thinking about it correctly 
    private async ValueTask<Subscription<TEvent>> Subscribe(Func<EventCall<TEvent>, Task> onEventExecute, Func<TEvent,Task>? onSubscribe = null, Func<Task>? onUnsubscribe = null, Action<Exception>? exceptionHandler = null)
    {
        var subscription = new Subscription<TEvent>((TEvent)this, onEventExecute, onSubscribe, onUnsubscribe, exceptionHandler);
        await AddSubscriber(subscription);
        return subscription;
    }
    
    public async ValueTask<Subscription<TEvent>> Subscribe<TEventCall>(Func<TEventCall, Task> onEventExecute, Func<TEvent,Task>? onSubscribe = null, Func<Task>? onUnsubscribe = null, Action<Exception>? exceptionHandler = null) 
    where TEventCall : EventCall<TEvent>
    {
        var subscription = new Subscription<TEvent,TEventCall>((TEvent)this, onEventExecute, onSubscribe, onUnsubscribe, exceptionHandler);
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

    private async Task DeleteEvent()
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

        if (!Subscribers.Add(subscription))
        {
            return;
        }
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

    protected async Task NotifySubscribers(EventCall<TEvent> eventCall)
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
                await ExecuteWithTimeout(eventCall, subscription, cts.Token);
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
                    await ExecuteWithTimeout(eventCall, subscription, cts.Token);
                }
                catch (Exception ex)
                {
                    subscription.TryHandleException(ex);
                }
            }
        }
        else
        {
            var tasks = new Task[Subscribers.Count];
            var i = 0;
            foreach (var subscription in Subscribers)
            {
                using var cts = new CancellationTokenSource(timeout);
                tasks[i++] = ExecuteWithTimeout(eventCall, subscription, cts.Token);
            }
            await Task.WhenAll(tasks);
        }
    }

    
    private async Task ExecuteWithTimeout(EventCall<TEvent> eventCall, Subscription<TEvent> subscription, CancellationToken cancellationToken)
    {
        try
        {
            var executionTask = subscription.HandleEventExecute(eventCall);
            
            // Get pooled TCS and ensure it's in a clean state
            //var tcs = s_tcsPool.Get();
            //tcs.TrySetCanceled(); // Cancel any previous state
            var newTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            
            try
            {
                using var timer = new Timer(s_timerCallback, newTcs, EventingConfiguration.EventingOptionsInternal.StartNextEventHandlerAfter, Timeout.InfiniteTimeSpan);
                
                // Wait for either completion or timeout
                var completedTask = await Task.WhenAny(executionTask, newTcs.Task);
                if (completedTask == newTcs.Task)
                {
                    // Timeout occurred - continue execution in background
                    DebugHelp.DebugWriteLine("Timeout occurred - continue execution in background");
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
                    // Execution completed before timeout
                    await executionTask;
                }
            }
            finally
            {
                //s_tcsPool.Return(tcs);
            }
        }
        catch (Exception ex)
        {
            DebugHelp.DebugWriteLine($"Event handler failed with error {ex.Message}");
            subscription.TryHandleException(ex);
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        await DeleteEvent();
    }
}