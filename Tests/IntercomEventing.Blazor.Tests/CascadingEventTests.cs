using Bunit;
using IntercomEventing.Blazor.Tests.Components;
using IntercomEventing.Blazor.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomEventing.Blazor.Tests;

/// <summary>
/// Events handed to <see cref="OrderTicker"/> as a cascading value, received through the subscribe-only
/// <see cref="IIntercomEvent{TCall}"/> view and kept in step with <c>ComponentEventSubscriptions.Resubscribe</c>.
/// </summary>
public class CascadingEventTests
{
    [Test]
    public async Task ChangedCascadingValue_MovesTheSubscriptionToTheNewEvent()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var firstEvent = new IntercomEvent<OrderPlaced>(TestEventOptions.WaitForEveryHandler);
        var secondEvent = new IntercomEvent<OrderPlaced>(TestEventOptions.WaitForEveryHandler);
        var host = context.Render<CascadedOrderHost>(parameters => parameters.Add(component => component.OrderEvent, firstEvent));
        var ticker = host.FindComponent<OrderTicker>();
        var firstSubscription = ticker.Instance.OrderSubscription;

        host.Render(parameters => parameters.Add(component => component.OrderEvent, secondEvent));
        await Task.Run(() => secondEvent.RaiseAsync(new OrderPlaced(1, 12m)));

        await Assert.That(firstSubscription).IsNotNull();
        await Assert.That(firstSubscription!.IsActive).IsFalse();
        await Assert.That(firstEvent.SubscriberCount).IsEqualTo(0);
        await Assert.That(secondEvent.SubscriberCount).IsEqualTo(1);
        await Assert.That(ticker.Find(".ticker").TextContent).IsEqualTo("Orders: 1, last total 12");
    }

    [Test]
    public async Task EventRegisteredInDependencyInjectionAndCascadedWithAddCascadingValue_ReachesTheSubscribeOnlyView()
    {
        //the framework pattern shown in the ComponentEventSubscriptions.Resubscribe remarks; the package ships no registration helper for it
        await using var context = BlazorTestContext.CreateInteractive(services =>
        {
            services.AddScoped(_ => new IntercomEvent<OrderPlaced>(TestEventOptions.WaitForEveryHandler));
            services.AddCascadingValue(serviceProvider => serviceProvider.GetRequiredService<IntercomEvent<OrderPlaced>>());
        });
        var ticker = context.Render<OrderTicker>();
        var orderPlaced = context.Services.GetRequiredService<IntercomEvent<OrderPlaced>>();

        await Task.Run(() => orderPlaced.RaiseAsync(new OrderPlaced(5, 3m)));

        await Assert.That(orderPlaced.SubscriberCount).IsEqualTo(1);
        await Assert.That(ticker.Find(".ticker").TextContent).IsEqualTo("Orders: 1, last total 3");
    }
}
