namespace IntercomEventing.Samples.WebApi.Features.Orders;

/// <summary>
/// Sends an order confirmation for every placed order. A hosted service owns its subscription for the lifetime of the app:
/// it subscribes in <see cref="StartAsync"/> and disposes the handle in <see cref="StopAsync"/>.
/// </summary>
/// <remarks>
/// Mail to an address ending in <c>.invalid</c> fails on purpose. The subscription has no exception handler of its own, so
/// the failure goes to <see cref="EventingOptions.DefaultExceptionHandler"/> (which <c>Program.cs</c> sends to the logger),
/// and the order is still placed.
/// </remarks>
public sealed class OrderConfirmationMailer : IHostedService
{
    //stands in for a call to a mail server
    private static readonly TimeSpan SimulatedSendTime = TimeSpan.FromMilliseconds(200);

    private readonly OrderService _orders;
    private readonly ILogger<OrderConfirmationMailer> _logger;
    private EventSubscription<EventCall<OrderPlaced>>? _subscription;

    public OrderConfirmationMailer(OrderService orders, ILogger<OrderConfirmationMailer> logger)
    {
        _orders = orders;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        //SubscribeAsync runs OnSubscribed before the handler is registered and returns the handle afterwards;
        //OnUnsubscribed runs once, whichever way the subscription is removed
        _subscription = await _orders.OrderPlaced.SubscribeAsync(SendConfirmationAsync, new()
        {
            OnSubscribed = LogSubscribedAsync,
            OnUnsubscribed = LogUnsubscribedAsync,
        }, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            //DisposeAsync waits for the OnUnsubscribed hook; Dispose() would start it in the background
            await _subscription.DisposeAsync();
        }
    }

    private async Task SendConfirmationAsync(EventCall<OrderPlaced> call)
    {
        var order = call.Payload;
        var isUndeliverable = order.CustomerEmail.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase);
        if (isUndeliverable)
        {
            throw new InvalidOperationException($"No mail server accepts mail for {order.CustomerEmail}.");
        }
        //cancelled only when the event is disposed (the app is shutting down)
        await Task.Delay(SimulatedSendTime, call.CancellationToken);
        _logger.LogInformation("Sent the confirmation of order {OrderNumber} to {CustomerEmail}", order.OrderNumber, order.CustomerEmail);
    }

    private Task LogSubscribedAsync(EventSubscription<EventCall<OrderPlaced>> subscription)
    {
        _logger.LogInformation("Order confirmations subscribed to {EventName} (subscription {SubscriptionId})", subscription.Event.Name, subscription.Id);
        return Task.CompletedTask;
    }

    private Task LogUnsubscribedAsync(EventSubscription<EventCall<OrderPlaced>> subscription)
    {
        _logger.LogInformation("Order confirmations unsubscribed from {EventName}", subscription.Event.Name);
        return Task.CompletedTask;
    }
}
