using Bunit;
using IntercomEventing.Blazor.Tests.Components;
using IntercomEventing.Blazor.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomEventing.Blazor.Tests;

/// <summary>
/// The declarative <see cref="EventListener{TCall}"/>, used by <see cref="OrderDashboard"/> with an explicit TCall and a
/// method group, and with TCall inferred from a typed lambda.
/// </summary>
public class EventListenerTests
{
    [Test]
    public async Task ForwardsEveryCallToOnRaisedOnTheRenderer_AndReRendersTheComponentThatSuppliedIt()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var orders = context.Services.GetRequiredService<OrderBook>();
        var probe = context.Services.GetRequiredService<RendererProbe>();
        var dashboard = context.Render<OrderDashboard>(parameters => parameters.Add(component => component.OrderEvent, orders.OrderPlaced));

        await Task.Run(() => orders.PlaceAsync(40m));
        await Task.Run(() => orders.PlaceAsync(2m));

        //one subscription per listener: the method-group listener and the typed-lambda listener
        await Assert.That(orders.OrderPlaced.SubscriberCount).IsEqualTo(2);
        await Assert.That(dashboard.Find(".last-order").TextContent).IsEqualTo("Last order: 2");
        await Assert.That(dashboard.Find(".total").TextContent).IsEqualTo("Total: 42");
        await Assert.That(probe.HandlerCalls).IsEqualTo(2);
        await Assert.That(probe.CallsOffTheRenderer).IsEqualTo(0);
    }

    [Test]
    public async Task ChangingTheEventParameter_MovesTheSubscriptionToTheNewEvent()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var firstEvent = new IntercomEvent<OrderPlaced>(TestEventOptions.WaitForEveryHandler);
        var secondEvent = new IntercomEvent<OrderPlaced>(TestEventOptions.WaitForEveryHandler);
        var dashboard = context.Render<OrderDashboard>(parameters => parameters.Add(component => component.OrderEvent, firstEvent));

        dashboard.Render(parameters => parameters.Add(component => component.OrderEvent, secondEvent));
        await Task.Run(() => firstEvent.RaiseAsync(new OrderPlaced(1, 5m)));
        var lastOrderAfterRaisingTheOldEvent = dashboard.Find(".last-order").TextContent;
        await Task.Run(() => secondEvent.RaiseAsync(new OrderPlaced(7, 5m)));

        await Assert.That(firstEvent.SubscriberCount).IsEqualTo(0);
        await Assert.That(secondEvent.SubscriberCount).IsEqualTo(2);
        await Assert.That(lastOrderAfterRaisingTheOldEvent).IsEqualTo("Last order: none");
        await Assert.That(dashboard.Find(".last-order").TextContent).IsEqualTo("Last order: 7");
    }

    [Test]
    public async Task SettingTheSameEventAgain_KeepsOneSubscriptionPerListener()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var orders = context.Services.GetRequiredService<OrderBook>();
        var probe = context.Services.GetRequiredService<RendererProbe>();
        var dashboard = context.Render<OrderDashboard>(parameters => parameters.Add(component => component.OrderEvent, orders.OrderPlaced));

        dashboard.Render(parameters => parameters.Add(component => component.OrderEvent, orders.OrderPlaced));
        await Task.Run(() => orders.PlaceAsync(3m));

        await Assert.That(orders.OrderPlaced.SubscriberCount).IsEqualTo(2);
        await Assert.That(probe.HandlerCalls).IsEqualTo(1);
    }

    [Test]
    public async Task NewOnRaisedForTheSameEvent_ReplacesTheOldCallback()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var orders = context.Services.GetRequiredService<OrderBook>();
        var firstCallbackCalls = 0;
        var secondCallbackCalls = 0;
        var listener = context.Render<EventListener<EventCall<OrderPlaced>>>(parameters => parameters
            .Add(component => component.Event, orders.OrderPlaced)
            .Add(component => component.OnRaised, (EventCall<OrderPlaced> call) => firstCallbackCalls++));

        //the event stays the same, so the subscription is kept; the listener reads OnRaised when a call arrives
        listener.Render(parameters => parameters.Add(component => component.OnRaised, (EventCall<OrderPlaced> call) => secondCallbackCalls++));
        await Task.Run(() => orders.PlaceAsync(1m));

        await Assert.That(orders.OrderPlaced.SubscriberCount).IsEqualTo(1);
        await Assert.That(firstCallbackCalls).IsEqualTo(0);
        await Assert.That(secondCallbackCalls).IsEqualTo(1);
    }

    [Test]
    public async Task NullEvent_Unsubscribes()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var orders = context.Services.GetRequiredService<OrderBook>();
        var dashboard = context.Render<OrderDashboard>(parameters => parameters.Add(component => component.OrderEvent, orders.OrderPlaced));

        dashboard.Render(parameters => parameters.Add(component => component.OrderEvent, null));

        await Assert.That(orders.OrderPlaced.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task ListenerRemovedFromTheRenderTree_Unsubscribes()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var orders = context.Services.GetRequiredService<OrderBook>();
        var probe = context.Services.GetRequiredService<RendererProbe>();
        var dashboard = context.Render<OrderDashboard>(parameters => parameters.Add(component => component.OrderEvent, orders.OrderPlaced));

        dashboard.Render(parameters => parameters.Add(component => component.ShowListeners, false));
        await Task.Run(() => orders.PlaceAsync(3m));

        await Assert.That(orders.OrderPlaced.SubscriberCount).IsEqualTo(0);
        await Assert.That(probe.HandlerCalls).IsEqualTo(0);
        await Assert.That(dashboard.Find(".last-order").TextContent).IsEqualTo("Last order: none");
    }
}
