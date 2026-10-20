using IntercomEventing.Samples.WebApi.Features.Catalog;
using IntercomEventing.Samples.WebApi.Features.Store;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IntercomEventing.Samples.WebApi.Features.Orders;

public sealed record PlaceOrderRequest(string CustomerEmail, string Sku, int Quantity);

public sealed record SalesProgress(decimal SalesToday, decimal Goal, bool GoalReached);

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/orders");
        orders.MapPost("/", PlaceOrderAsync);
        orders.MapGet("/", GetOrders);
        orders.MapGet("/sales", GetSalesProgress);
        orders.MapPost("/sales/new-day", StartNewSalesDay);
        return app;
    }

    private static async Task<Results<Created<OrderPlaced>, Conflict<string>, NotFound<string>, ValidationProblem>> PlaceOrderAsync(
        PlaceOrderRequest request, OrderService orders, StoreService store, CatalogService catalog)
    {
        if (request.Quantity < 1 || string.IsNullOrWhiteSpace(request.CustomerEmail))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["order"] = ["An order needs a customer email and a quantity of at least 1."],
            });
        }
        if (store.CurrentStatus != StoreStatus.Open)
        {
            return TypedResults.Conflict($"The store is {store.CurrentStatus}; orders are not accepted.");
        }
        if (!catalog.TryGetProduct(request.Sku, out var product))
        {
            return TypedResults.NotFound($"Unknown product {request.Sku}.");
        }

        //the response is sent after the handlers ran (the confirmation mail takes about 200 ms)
        var order = await orders.PlaceAsync(request, product);
        return TypedResults.Created($"/orders/{order.OrderNumber}", order);
    }

    private static Ok<IReadOnlyList<OrderPlaced>> GetOrders(OrderService orders)
    {
        return TypedResults.Ok(orders.Orders);
    }

    private static Ok<SalesProgress> GetSalesProgress(OrderService orders)
    {
        var goalReached = orders.SalesToday >= orders.SalesGoal;
        return TypedResults.Ok(new SalesProgress(orders.SalesToday, orders.SalesGoal, goalReached));
    }

    private static NoContent StartNewSalesDay(OrderService orders)
    {
        orders.StartNewSalesDay();
        return TypedResults.NoContent();
    }
}
