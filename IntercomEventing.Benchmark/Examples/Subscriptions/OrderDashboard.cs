using System.Collections.Concurrent;

namespace IntercomEventing.Benchmark.Examples.Subscriptions;

/// <summary>
/// A subscriber with a lifetime: it keeps a handle for each subscription and removes them all when it is disposed.
/// </summary>
public sealed class OrderDashboard : IAsyncDisposable
{
    //handles of different events stored together through the untyped IEventSubscription
    private readonly List<IEventSubscription> _subscriptions = [];
    private readonly ConcurrentDictionary<int, decimal> _openOrders = new();
    private readonly ConcurrentQueue<string> _lifecycleLog = new();

    public int OpenOrderCount => _openOrders.Count;

    public IReadOnlyCollection<string> LifecycleLog => _lifecycleLog;

    public async Task StartAsync(OrderBook orders)
    {
        //Subscribe: registers at once and returns the handle; a synchronous handler binds to the Action overload
        _subscriptions.Add(orders.OrderPlaced.Subscribe(OnOrderPlaced));

        //SubscribeAsync: lifecycle hooks and a per-subscription exception handler; new() { ... } infers the call type
        var cancellationSubscription = await orders.OrderCancelled.SubscribeAsync(OnOrderCancelledAsync, new()
        {
            OnSubscribed = subscription => RecordLifecycleAsync($"subscribed to {subscription.Event.Name}"),
            OnUnsubscribed = subscription => RecordLifecycleAsync($"unsubscribed from {subscription.Event.Name}"),
            OnException = (exception, call) => RecordLifecycle($"could not cancel order {call.Payload.OrderNumber}: {exception.Message}"),
        });
        _subscriptions.Add(cancellationSubscription);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions)
        {
            //DisposeAsync awaits the OnUnsubscribed hook; Dispose() would start it in the background
            await subscription.DisposeAsync();
        }
        _subscriptions.Clear();
    }

    private void OnOrderPlaced(EventCall<OrderPlaced> call)
    {
        _openOrders[call.Payload.OrderNumber] = call.Payload.Total;
    }

    private async Task OnOrderCancelledAsync(EventCall<OrderCancelled> call)
    {
        //stands in for refreshing the dashboard from a store
        await Task.Delay(TimeSpan.FromMilliseconds(5), call.CancellationToken);
        if (!_openOrders.TryRemove(call.Payload.OrderNumber, out _))
        {
            throw new InvalidOperationException($"order {call.Payload.OrderNumber} is not open");
        }
    }

    private Task RecordLifecycleAsync(string entry)
    {
        RecordLifecycle(entry);
        return Task.CompletedTask;
    }

    private void RecordLifecycle(string entry)
    {
        _lifecycleLog.Enqueue(entry);
        Console.WriteLine($"Dashboard {entry}");
    }
}
