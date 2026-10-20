namespace IntercomEventing.Benchmark.Examples.Subscriptions;

public sealed record OrderPlaced(int OrderNumber, decimal Total);

public sealed record OrderCancelled(int OrderNumber, string Reason);

/// <summary>
/// The producer used by the subscription example: two events with different payloads.
/// </summary>
public sealed class OrderBook : IAsyncDisposable
{
    private int _lastOrderNumber;

    public IntercomEvent<OrderPlaced> OrderPlaced { get; } = new() { Name = "OrderPlaced" };

    public IntercomEvent<OrderCancelled> OrderCancelled { get; } = new() { Name = "OrderCancelled" };

    /// <returns>The new order's number.</returns>
    public async Task<int> PlaceAsync(decimal total)
    {
        var orderNumber = Interlocked.Increment(ref _lastOrderNumber);
        await OrderPlaced.RaiseAsync(new OrderPlaced(orderNumber, total), caller: this);
        return orderNumber;
    }

    public Task CancelAsync(int orderNumber, string reason)
    {
        return OrderCancelled.RaiseAsync(new OrderCancelled(orderNumber, reason), caller: this);
    }

    /// <summary>
    /// Disposing an event removes its subscriptions and waits for unsubscribe hooks that are still running.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await OrderPlaced.DisposeAsync();
        await OrderCancelled.DisposeAsync();
    }
}
