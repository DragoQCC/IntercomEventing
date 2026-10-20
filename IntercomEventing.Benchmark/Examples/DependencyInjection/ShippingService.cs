namespace IntercomEventing.Benchmark.Examples.DependencyInjection;

public sealed record OrderShipped(int OrderNumber, string Carrier);

/// <summary>
/// A singleton producer. Its event is created without options, so every raise uses <see cref="EventingOptions.Default"/>,
/// which <c>AddIntercomEventing</c> sets for the application.
/// </summary>
public sealed class ShippingService : IAsyncDisposable
{
    public IntercomEvent<OrderShipped> OrderShipped { get; } = new() { Name = "OrderShipped" };

    public Task ShipAsync(int orderNumber, string carrier)
    {
        return OrderShipped.RaiseAsync(new OrderShipped(orderNumber, carrier), caller: this);
    }

    //the container disposes singletons when the host is disposed
    public ValueTask DisposeAsync()
    {
        return OrderShipped.DisposeAsync();
    }
}
