using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IntercomEventing.Samples.WebApi.Features.Catalog;
using IntercomEventing.Samples.WebApi.Features.Orders;
using IntercomEventing.Samples.WebApi.Features.Store;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IntercomEventing.Samples.WebApi.Features.LiveFeed;

public sealed record LiveFeedMessage(string EventName, Guid EventCallId, DateTimeOffset RaisedAt, string Summary);

/// <summary>
/// <c>GET /events/stream</c>: a Server-Sent Events stream of the business events. Each connected client gets its own
/// subscriptions, which are removed when the client disconnects. Watch it with <c>curl -N http://localhost:5281/events/stream</c>.
/// </summary>
public static class LiveFeedEndpoints
{
    //how many messages a slow client may fall behind before the oldest are dropped
    private const int MaxQueuedMessagesPerClient = 100;

    public static IEndpointRouteBuilder MapLiveFeedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/events/stream", StreamEvents);
        return app;
    }

    private static ServerSentEventsResult<LiveFeedMessage> StreamEvents(OrderService orders, StoreService store,
        PriceListPublisher priceLists, CancellationToken requestAborted)
    {
        return TypedResults.ServerSentEvents(ReadEventsAsync(orders, store, priceLists, requestAborted));
    }

    private static async IAsyncEnumerable<SseItem<LiveFeedMessage>> ReadEventsAsync(OrderService orders, StoreService store,
        PriceListPublisher priceLists, [EnumeratorCancellation] CancellationToken requestAborted)
    {
        //the handlers only queue a message, so a slow client never holds up a raise (or the request that raised it)
        var queuedMessages = Channel.CreateBounded<SseItem<LiveFeedMessage>>(new BoundedChannelOptions(MaxQueuedMessagesPerClient)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        //one subscription per event for this client; await using removes them when the client disconnects (requestAborted
        //ends the loop below) or the stream fails
        await using var orderSubscription = orders.OrderPlaced.Subscribe(call =>
            queuedMessages.Writer.TryWrite(ToSseItem("order-placed", call, $"Order {call.Payload.OrderNumber}: {call.Payload.Quantity} x {call.Payload.Sku} for {call.Payload.Total:0.00}")));
        await using var salesGoalSubscription = orders.DailySalesGoalReached.Subscribe(call =>
            queuedMessages.Writer.TryWrite(ToSseItem("sales-goal-reached", call, $"Sales goal {call.Threshold:0.00} reached by order {call.Payload.OrderNumber} ({call.CurrentValue:0.00} today)")));
        await using var storeSubscription = store.StatusChanged.Subscribe(call =>
            queuedMessages.Writer.TryWrite(ToSseItem("store-status-changed", call, $"Store {call.OldState} -> {call.NewState}: {call.Payload.Reason}")));
        await using var priceListSubscription = priceLists.PriceListPublished.Subscribe(call =>
            queuedMessages.Writer.TryWrite(ToSseItem("price-list-published", call, $"Price list v{call.Payload.Version} published with {call.Payload.ChangesIncluded} change(s)")));

        await foreach (var message in queuedMessages.Reader.ReadAllAsync(requestAborted))
        {
            yield return message;
        }
    }

    private static SseItem<LiveFeedMessage> ToSseItem(string eventType, EventCall call, string summary)
    {
        var metadata = call.Metadata;
        var message = new LiveFeedMessage(metadata.EventName, metadata.EventCallId, metadata.RaisedAt, summary);
        //the call id doubles as the SSE id: version 7 GUIDs sort by raise time
        return new SseItem<LiveFeedMessage>(message, eventType) { EventId = metadata.EventCallId.ToString() };
    }
}
