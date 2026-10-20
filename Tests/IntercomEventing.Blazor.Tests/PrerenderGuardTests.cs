using Bunit;
using IntercomEventing.Blazor.Tests.Components;
using IntercomEventing.Blazor.Tests.TestSupport;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomEventing.Blazor.Tests;

/// <summary>
/// Subscribing while a component renders statically (static server rendering or prerendering) subscribes nothing.
/// </summary>
public class PrerenderGuardTests
{
    [Test]
    public async Task StaticRendering_SubscribesNothing_ThroughTheBaseClassOrTheListener()
    {
        await using var context = BlazorTestContext.CreateWithoutRendererInfo();
        context.SetRendererInfo(new RendererInfo("Static", isInteractive: false));
        var users = context.Services.GetRequiredService<UserDirectory>();
        var orders = context.Services.GetRequiredService<OrderBook>();

        var feed = context.Render<UserFeed>();
        context.Render<OrderDashboard>(parameters => parameters.Add(component => component.OrderEvent, orders.OrderPlaced));

        await Assert.That(feed.Instance.UserJoinedSubscription).IsNull();
        await Assert.That(users.UserJoined.SubscriberCount).IsEqualTo(0);
        await Assert.That(orders.OrderPlaced.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task InteractiveWebAssemblyRendering_Subscribes()
    {
        await using var context = BlazorTestContext.CreateWithoutRendererInfo();
        context.SetRendererInfo(new RendererInfo("WebAssembly", isInteractive: true));
        var users = context.Services.GetRequiredService<UserDirectory>();

        var feed = context.Render<UserFeed>();
        await Task.Run(() => users.AddAsync("Ada"));

        await Assert.That(users.UserJoined.SubscriberCount).IsEqualTo(1);
        await Assert.That(feed.Find("li").TextContent).IsEqualTo("Ada");
    }

    [Test]
    public async Task RendererThatCannotReportItsInfo_IsTreatedAsInteractive()
    {
        //bUnit throws MissingRendererInfoException from RendererInfo until SetRendererInfo is called
        await using var context = BlazorTestContext.CreateWithoutRendererInfo();
        var users = context.Services.GetRequiredService<UserDirectory>();

        var feed = context.Render<UserFeed>();

        await Assert.That(feed.Instance.UserJoinedSubscription).IsNotNull();
        await Assert.That(users.UserJoined.SubscriberCount).IsEqualTo(1);
    }
}
