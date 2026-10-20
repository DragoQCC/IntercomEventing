namespace IntercomEventing.PackageConsumer;

public sealed record OrderPlaced(int OrderNumber, decimal Total);

/// <summary>
/// A producer whose event members come from the [IntercomEvent] generator, which reaches this project only through the
/// IntercomEventing.Blazor package.
/// </summary>
public sealed partial class OrderBook
{
    [IntercomEvent]
    private readonly IntercomEvent<OrderPlaced> _orderPlaced = new();

    private int _lastOrderNumber;

    public Task PlaceAsync(decimal total)
    {
        var orderNumber = Interlocked.Increment(ref _lastOrderNumber);
        return _orderPlaced.RaiseAsync(new OrderPlaced(orderNumber, total), caller: this);
    }
}
