using IntercomEventing.Samples.WebApi.Features.Catalog;

namespace IntercomEventing.Samples.WebApi.Features.Orders;

public sealed record OrderPlaced(int OrderNumber, string CustomerEmail, string Sku, int Quantity, decimal Total, DateTimeOffset PlacedAt);

/// <summary>
/// Places orders and owns two events, exposed through their subscribe-only views:
/// <list type="bullet">
/// <item><description><see cref="OrderPlaced"/>, an <see cref="IntercomEvent{TPayload}"/> raised for every order;</description></item>
/// <item><description><see cref="DailySalesGoalReached"/>, a <see cref="ThresholdEvent{TValue, TPayload}"/> that adds up the
/// day's sales and raises once, on the order that reaches <see cref="SalesGoal"/>.</description></item>
/// </list>
/// </summary>
public sealed class OrderService : IAsyncDisposable
{
    private const decimal DailySalesGoal = 500m;

    private readonly Lock _ordersLock = new();
    private readonly List<OrderPlaced> _orders = [];
    private readonly IntercomEvent<OrderPlaced> _orderPlaced = new() { Name = "OrderPlaced" };
    private readonly ThresholdEvent<decimal, OrderPlaced> _salesGoalReached = new(DailySalesGoal, ThresholdRaiseMode.OnlyWhenCrossed)
    {
        Name = "DailySalesGoalReached",
    };
    private int _lastOrderNumber = 1000;

    public IIntercomEvent<EventCall<OrderPlaced>> OrderPlaced => _orderPlaced;

    public IIntercomEvent<ThresholdEventCall<decimal, OrderPlaced>> DailySalesGoalReached => _salesGoalReached;

    public decimal SalesGoal => DailySalesGoal;

    public decimal SalesToday => _salesGoalReached.CurrentValue;

    public IReadOnlyList<OrderPlaced> Orders
    {
        get
        {
            lock (_ordersLock)
            {
                return _orders.ToArray();
            }
        }
    }

    /// <summary>
    /// Records the order, then raises <see cref="OrderPlaced"/> and adds the total to the day's sales. Completes once every
    /// handler has finished, or has been moved to the background after <see cref="EventingOptions.StartNextEventHandlerAfter"/>.
    /// A failing handler never fails the order.
    /// </summary>
    public async Task<OrderPlaced> PlaceAsync(PlaceOrderRequest request, Product product)
    {
        var orderNumber = Interlocked.Increment(ref _lastOrderNumber);
        var total = product.Price * request.Quantity;
        var order = new OrderPlaced(orderNumber, request.CustomerEmail, product.Sku, request.Quantity, total, DateTimeOffset.UtcNow);
        lock (_ordersLock)
        {
            _orders.Add(order);
        }

        //no request token on purpose: a client that disconnects must not cancel the confirmation mail of a placed order
        await _orderPlaced.RaiseAsync(order, caller: this);
        //raises only when this order takes the running total from below the goal to at or past it
        await _salesGoalReached.IncrementAsync(total, order, caller: this);
        return order;
    }

    /// <summary>
    /// Starts a new sales day: sets the running total back to zero without raising, so the goal can be reached again.
    /// </summary>
    public void StartNewSalesDay()
    {
        _salesGoalReached.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        await _orderPlaced.DisposeAsync();
        await _salesGoalReached.DisposeAsync();
    }
}
