using HelpfulTypesAndExtensions;
using JetBrains.Annotations;

namespace IntercomEventing.Features.Events;

/// <summary>
/// Represents an event that can be raised and subscribed to <br/>
/// The GenericEvent class is the base class for all events
/// </summary>
/// <typeparam name="TEvent"></typeparam>
public abstract record GenericEvent<TEvent> : IAsyncDisposable where TEvent : GenericEvent<TEvent>
{
    /// <summary>
    /// The ID of the event <br/>
    /// This ID is consistent for all event calls of the same event instance
    /// </summary>
    public Guid Id { get; } = Guid.CreateVersion7(DateTime.UtcNow);
    
    private HashSet<Subscription<TEvent>> Subscribers { get; } = new();
    private readonly List<Task> _backgroundTasks = new();
    private readonly SemaphoreSlim _backgroundTasksLock = new(1);
    
    /// <summary>
    /// Subscribes to the event
    /// </summary>
    /// <param name="onEventExecute"> The action to execute when the event is raised </param>
    /// <param name="onSubscribe"> The action to execute when the subscription is created </param>
    /// <param name="onUnsubscribe"> The action to execute when the subscription is removed </param>
    /// <param name="exceptionHandler"> The action to execute when an exception is thrown </param>
    /// <typeparam name="TEventCall"> The type of event call to subscribe to </typeparam>
    /// <returns> The subscription for the event, which can be used to unsubscribe from the event </returns>
    public async ValueTask<Subscription<TEvent>> Subscribe<TEventCall>(Func<TEventCall, Task> onEventExecute, Func<TEvent,Task>? onSubscribe = null, Func<Task>? onUnsubscribe = null, Action<Exception>? exceptionHandler = null) 
    where TEventCall : EventCall<TEvent>
    {
        var subscription = new Subscription<TEvent,TEventCall>((TEvent)this, onEventExecute, onSubscribe, onUnsubscribe, exceptionHandler);
        await AddSubscriber(subscription);
        return subscription;
    }

    /// <summary>
    /// Unsubscribes from the event
    /// </summary>
    /// <param name="subscription"> The subscription to unsubscribe from the event </param>
    /// <returns> true if the subscription was removed, otherwise false </returns>
    public async Task<bool> Unsubscribe(Subscription<TEvent> subscription)
    {
        if(Subscribers.Contains(subscription))
        {
            await RemoveSubscriber(subscription);
            return true;
        }
        return false;
    }
    
    
    /// <summary>
    /// Checks if the subscription is still active <br/>
    /// </summary>
    /// <param name="subscription">The subscription to check</param>
    /// <returns>true if the subscription is still active, otherwise false</returns>
    [UsedImplicitly]
    public bool IsSubscribed(Subscription<TEvent> subscription) => Subscribers.Contains(subscription);

    
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

    [UsedImplicitly]
    abstract protected EventCall<TEvent> CreateEventCall();
    
    /// <summary>
    /// Fires the event by invoking all subscribed event handlers
    /// </summary>
    /// <param name="eventCall"> The event call to raise (i.e., the event data you want to pass to the event handlers)</param>
    /// <param name="eventCaller"> The object that raised the event (optional) </param>
    /// <typeparam name="TEventCall"> The type of event call to raise </typeparam>
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
    
    protected async Task NotifySubscribers(EventCall<TEvent> eventCall)
    {
        // Fast path for single subscriber
        if (Subscribers.Count == 1)
        {
            var subscription = Subscribers.First();
            await ExecuteWithTimeout(eventCall, subscription);
            return;
        }
        var options = EventingConfiguration.EventingOptionsInternal;

        if (EventingConfiguration.IsSeq)
        {
            foreach (var subscription in Subscribers)
            {
                await ExecuteWithTimeout(eventCall, subscription);
            }
        }
        else
        {
            List<Lazy<Task>> tasks = new(Subscribers.Count);
            foreach (var subscription in Subscribers)
            {
                tasks.Add(new Lazy<Task>(() => ExecuteWithTimeout(eventCall, subscription)));
            }
            //if the max number of concurrent handlers is lower then the number of tasks to run we need to run in batches
            if (options.MaxNumberOfConcurrentHandlers < tasks.Count)
            {
                DebugHelp.DebugWriteLine($"Max number of concurrent handlers is {options.MaxNumberOfConcurrentHandlers}");
                for (int j = 0; j < tasks.Count; j += options.MaxNumberOfConcurrentHandlers)
                {
                    var batch = tasks.Skip(j).Take(options.MaxNumberOfConcurrentHandlers).ToArray();
                    DebugHelp.DebugWriteLine($"Running batch number {j} of {batch.Length} event handlers in parallel");
                    await Task.WhenAll(batch.Select(t => t.Value));
                }
            }
            else
            {
                DebugHelp.DebugWriteLine($"Max number of concurrent handlers is {options.MaxNumberOfConcurrentHandlers}");
                DebugHelp.DebugWriteLine($"Running {tasks.Count} event handlers in parallel");
                await Task.WhenAll(tasks.Select(t => t.Value));
            }
        }
    }
    
    private async Task ExecuteWithTimeout(EventCall<TEvent> eventCall, Subscription<TEvent> subscription)
    {
        Lazy<Task> executionTask = new(() => subscription.HandleEventExecute(eventCall));
        //Automatically cancels if the CT timer expires
        //Add a small buffer to the timeout to prevent cases where the timeout is exactly the same as the event handler execution time
        var timeoutValue = EventingConfiguration.EventingOptionsInternal.StartNextEventHandlerAfter + TimeSpan.FromMilliseconds(50);
        var cancellationTokenSource = new CancellationTokenSource(timeoutValue);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationTokenSource.Token.Register(() => tcs.TrySetResult(true));
        
        try
        {
            // Wait for either completion or timeout
            var completedTask = await Task.WhenAny(executionTask.Value, tcs.Task);
            if (completedTask == tcs.Task && tcs.Task.Result is true)
            {
                // Timeout occurred - continue execution in the background
                DebugHelp.DebugWriteLine($"Timeout of {timeoutValue.Seconds} seconds occurred - continuing execution in background and starting next event handler");
                var backgroundTask = Task.Run(async () =>
                {
                    try
                    {
                        await executionTask.Value;
                    }
                    catch (Exception ex)
                    {
                        subscription.TryHandleException(ex);
                    }
                });
                await _backgroundTasksLock.WaitAsync();
                try
                {
                    _backgroundTasks.Add(backgroundTask);
                    // Clean up completed tasks
                    _backgroundTasks.RemoveAll(t => t.IsCompleted);
                }
                finally
                {
                    _backgroundTasksLock.Release();
                }
            }
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }
    }

    private async Task DeleteEvent()
    {
        foreach (var subscription in Subscribers)
        {
            await subscription.DisposeAsync();
        }
        Subscribers.Clear();
    }
    
    /// <summary>
    /// Disposes the event and all associated resources <br/>
    /// This will unsubscribe all subscribers and clean up any resources used by the event
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        // Wait for all background tasks to complete before disposing
        await _backgroundTasksLock.WaitAsync();
        try
        {
            if (_backgroundTasks.Count > 0)
            {
                DebugHelp.DebugWriteLine($"Waiting for {_backgroundTasks.Count} background tasks to complete");
                await Task.WhenAll(_backgroundTasks);
                _backgroundTasks.Clear();
            }
        }
        finally
        {
            _backgroundTasksLock.Release();
            _backgroundTasksLock.Dispose();
        }
        await DeleteEvent();
    }
}