namespace IntercomEventing.Benchmark.Examples.Subscriptions;

/// <summary>
/// Every way to subscribe and unsubscribe: <c>Raised +=</c>/<c>-=</c>, <c>Subscribe</c> with a handle,
/// <c>SubscribeAsync</c> with hooks, <c>Unsubscribe(handler)</c>, and the difference between <c>Dispose()</c> and
/// <c>DisposeAsync()</c> on a handle.
/// </summary>
internal sealed class SubscriptionsExample : IExample
{
    public string Name => "Subscriptions";

    public string Summary => "Raised +=, Subscribe, SubscribeAsync with hooks, handles and unsubscribe semantics";

    public async Task RunAsync()
    {
        //await using, so a failed check still disposes both; their DisposeAsync is idempotent, so the explicit calls below stay
        await using var orders = new OrderBook();
        var auditLog = new AuditLog();
        await using var dashboard = new OrderDashboard();

        ExampleCheck.Step("Raised += with one base-type handler on two events, and a dashboard that keeps handles");
        orders.OrderPlaced.Raised += auditLog.RecordAsync;
        orders.OrderCancelled.Raised += auditLog.RecordAsync;
        await dashboard.StartAsync(orders);
        ExampleCheck.That(dashboard.LifecycleLog.First() == "subscribed to OrderCancelled", "OnSubscribed ran before SubscribeAsync returned");

        ExampleCheck.Step("Placing two orders, cancelling one, and cancelling an order that does not exist");
        var firstOrder = await orders.PlaceAsync(25m);
        await orders.PlaceAsync(40m);
        await orders.CancelAsync(firstOrder, "customer request");
        await orders.CancelAsync(99, "typo");
        ExampleCheck.That(dashboard.OpenOrderCount == 1, "the dashboard tracked one open order");
        ExampleCheck.That(auditLog.Entries.Count == 4, "the base-type handler received all four calls");
        ExampleCheck.That(dashboard.LifecycleLog.Contains("could not cancel order 99: order 99 is not open"), "the failing call went to that subscription's OnException");

        ExampleCheck.Step("Removing the audit handler with -= and with Unsubscribe(handler)");
        orders.OrderPlaced.Raised -= auditLog.RecordAsync;
        var removedFromCancelled = orders.OrderCancelled.Unsubscribe(auditLog.RecordAsync);
        var removedAgain = orders.OrderCancelled.Unsubscribe(auditLog.RecordAsync);
        var removedExactlyOnce = removedFromCancelled && !removedAgain;
        ExampleCheck.That(removedExactlyOnce, "Unsubscribe reports whether it removed a subscription");

        ExampleCheck.Step("A subscription that lives for one using block");
        var ordersSeenInScope = 0;
        using (orders.OrderPlaced.Subscribe(call => ordersSeenInScope++))
        {
            await orders.PlaceAsync(5m);
        }
        await orders.PlaceAsync(6m);
        ExampleCheck.That(ordersSeenInScope == 1, "the handler received calls only while the handle was alive");
        ExampleCheck.That(auditLog.Entries.Count == 4, "the removed audit handler received nothing more");

        ExampleCheck.Step("Disposing the dashboard: DisposeAsync awaits each OnUnsubscribed hook");
        await dashboard.DisposeAsync();
        ExampleCheck.That(dashboard.LifecycleLog.Last() == "unsubscribed from OrderCancelled", "the hook had finished when DisposeAsync returned");
        var noSubscribersLeft = orders.OrderPlaced.SubscriberCount == 0 && orders.OrderCancelled.SubscriberCount == 0;
        ExampleCheck.That(noSubscribersLeft, "both events have no subscribers left");

        await ShowDisposeStartsTheHookInTheBackgroundAsync(orders);
    }

    private static async Task ShowDisposeStartsTheHookInTheBackgroundAsync(OrderBook orders)
    {
        ExampleCheck.Step("Dispose() removes the handler at once and leaves a slow OnUnsubscribed hook running");
        var releaseHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookFinished = false;
        var subscription = await orders.OrderPlaced.SubscribeAsync(call => { }, new()
        {
            OnUnsubscribed = async _ =>
            {
                //stands in for flushing a buffer to slow storage
                await releaseHook.Task;
                hookFinished = true;
            },
        });

        try
        {
            subscription.Dispose();
            var removedButHookStillRunning = !subscription.IsActive && !hookFinished;
            ExampleCheck.That(removedButHookStillRunning, "Dispose() removed the handler and returned without waiting for the hook");
        }
        finally
        {
            //released even when the check failed: the event's disposal waits for this hook, which would otherwise wait forever
            releaseHook.TrySetResult();
        }
        //disposing the event waits for unsubscribe hooks that nobody awaited
        await orders.DisposeAsync();
        ExampleCheck.That(hookFinished, "the event's DisposeAsync waited for the background hook");
    }
}
