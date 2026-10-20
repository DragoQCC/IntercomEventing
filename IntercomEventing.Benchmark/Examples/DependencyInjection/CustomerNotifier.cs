using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace IntercomEventing.Benchmark.Examples.DependencyInjection;

/// <summary>
/// A hosted service that subscribes when the host starts and unsubscribes when it stops.
/// </summary>
public sealed class CustomerNotifier : IHostedService
{
    private static readonly string[] SupportedCarriers = ["DHL", "UPS"];

    private readonly ShippingService _shipping;
    private readonly ConcurrentQueue<int> _notifiedOrders = new();
    private EventSubscription<EventCall<OrderShipped>>? _subscription;

    public IReadOnlyCollection<int> NotifiedOrders => _notifiedOrders;

    public CustomerNotifier(ShippingService shipping)
    {
        _shipping = shipping;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _shipping.OrderShipped.Subscribe(OnOrderShippedAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
        }
    }

    private async Task OnOrderShippedAsync(EventCall<OrderShipped> call)
    {
        var shipment = call.Payload;
        if (!SupportedCarriers.Contains(shipment.Carrier))
        {
            //no exception handler on this subscription, so EventingOptions.Default.DefaultExceptionHandler receives it
            throw new NotSupportedException($"no tracking link for carrier {shipment.Carrier}");
        }
        //stands in for sending the customer a tracking mail
        await Task.Delay(TimeSpan.FromMilliseconds(5), call.CancellationToken);
        _notifiedOrders.Enqueue(shipment.OrderNumber);
        Console.WriteLine($"Customer notified: order {shipment.OrderNumber} shipped with {shipment.Carrier}");
    }
}
