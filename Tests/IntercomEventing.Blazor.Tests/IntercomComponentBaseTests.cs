using Bunit;
using IntercomEventing.Blazor.Tests.Components;
using IntercomEventing.Blazor.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomEventing.Blazor.Tests;

/// <summary>
/// Components deriving from <see cref="IntercomComponentBase"/>, rendered by bUnit's real renderer and dispatcher.
/// </summary>
public class IntercomComponentBaseTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task HandlerRaisedFromAThreadPoolThread_RunsOnTheRendererDispatcher_AndReRendersTheComponent()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var users = context.Services.GetRequiredService<UserDirectory>();
        var probe = context.Services.GetRequiredService<RendererProbe>();
        var feed = context.Render<UserFeed>();
        var rendersBeforeRaising = feed.RenderCount;

        //raised from thread-pool threads, as a hub connection or a background service would
        await Task.Run(() => users.AddAsync("Ada"));
        await Task.Run(() => users.AddAsync("Grace"));

        var renderedNames = string.Join(",", feed.FindAll("li").Select(item => item.TextContent));
        await Assert.That(probe.HandlerCalls).IsEqualTo(2);
        await Assert.That(probe.CallsOffTheRenderer).IsEqualTo(0);
        await Assert.That(feed.RenderCount).IsEqualTo(rendersBeforeRaising + 2);
        await Assert.That(renderedNames).IsEqualTo("Grace,Ada");
    }

    [Test]
    public async Task ConcurrentRaisesFromThreadPoolThreads_AreAllHandledOneAtATimeOnTheRendererDispatcher()
    {
        const int RaiseCount = 50;
        await using var context = BlazorTestContext.CreateInteractive();
        var users = context.Services.GetRequiredService<UserDirectory>();
        var probe = context.Services.GetRequiredService<RendererProbe>();
        var feed = context.Render<UserFeed>();

        //the handler mutates a List<string> without a lock; that is only safe because the dispatcher runs one call at a time
        var raises = Enumerable.Range(0, RaiseCount).Select(index => Task.Run(() => users.AddAsync($"user {index}")));
        await Task.WhenAll(raises);

        await Assert.That(probe.HandlerCalls).IsEqualTo(RaiseCount);
        await Assert.That(probe.CallsOffTheRenderer).IsEqualTo(0);
        await Assert.That(feed.FindAll("li").Count).IsEqualTo(RaiseCount);
    }

    [Test]
    public async Task AsynchronousHandler_RendersAtItsFirstAwait_AndAgainWhenItFinishes()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var orders = context.Services.GetRequiredService<OrderBook>();
        var gate = context.Services.GetRequiredService<HandlerGate>();
        var probe = context.Services.GetRequiredService<RendererProbe>();
        var progress = context.Render<OrderProgress>();
        var rendersBeforeRaising = progress.RenderCount;

        var raise = Task.Run(() => orders.PlaceAsync(10m));
        await progress.WaitForStateAsync(() => progress.Find(".status").TextContent == "loading order 1", WaitTimeout);
        var rendersWhileTheHandlerWaits = progress.RenderCount;
        gate.Release();
        await raise;

        await Assert.That(rendersWhileTheHandlerWaits).IsEqualTo(rendersBeforeRaising + 1);
        await Assert.That(progress.RenderCount).IsEqualTo(rendersBeforeRaising + 2);
        await Assert.That(progress.Find(".status").TextContent).IsEqualTo("order 1 loaded");
        await Assert.That(probe.CallsOffTheRenderer).IsEqualTo(0);
    }

    [Test]
    public async Task DisposingTheComponents_RemovesTheirSubscriptions()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var users = context.Services.GetRequiredService<UserDirectory>();
        var orders = context.Services.GetRequiredService<OrderBook>();
        var probe = context.Services.GetRequiredService<RendererProbe>();
        context.Render<UserFeed>();
        context.Render<OrderProgress>();
        var subscribersWhileRendered = users.UserJoined.SubscriberCount + orders.OrderPlaced.SubscriberCount;

        await context.DisposeComponentsAsync();
        await Task.Run(() => users.AddAsync("Linus"));

        await Assert.That(subscribersWhileRendered).IsEqualTo(2);
        await Assert.That(users.UserJoined.SubscriberCount).IsEqualTo(0);
        await Assert.That(orders.OrderPlaced.SubscriberCount).IsEqualTo(0);
        await Assert.That(probe.HandlerCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Disposal_RemovesTheSubscriptionsFirst_ThenRunsDisposeAsyncCore_ThenTheComponentsOwnDispose()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var source = new IntercomEvent<string>(TestEventOptions.WaitForEveryHandler);
        var recorder = context.Render<DisposalRecorder>(parameters => parameters.Add(component => component.Source, source));
        var component = recorder.Instance;
        var subscribersWhileRendered = source.SubscriberCount;

        await context.DisposeComponentsAsync();

        await Assert.That(subscribersWhileRendered).IsEqualTo(1);
        await Assert.That(string.Join("; ", component.DisposalSteps))
            .IsEqualTo("DisposeAsyncCore saw 0 subscribers; Dispose saw 0 subscribers");
    }

    [Test]
    public async Task FailingUnsubscribeHook_DoesNotFailComponentDisposal_AndIsReportedToTheEventsExceptionHandler()
    {
        var eventFailures = new EventFailureRecorder();
        var source = new IntercomEvent<string>(eventFailures.EventOptions);
        await using var context = BlazorTestContext.CreateInteractive();
        var subscriber = context.Render<HookedSubscriber>(parameters => parameters.Add(component => component.Source, source));
        await subscriber.WaitForStateAsync(() => subscriber.Find(".hooked").TextContent == "subscribed", WaitTimeout);
        var component = subscriber.Instance;

        await context.DisposeComponentsAsync();
        var failure = await eventFailures.WaitForFirstFailureAsync();

        await Assert.That(component.UnsubscribeHookRan).IsTrue();
        await Assert.That(source.SubscriberCount).IsEqualTo(0);
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.OnUnsubscribed);
        await Assert.That(failure.Exception.Message).IsEqualTo("unsubscribe hook failed");
        await Assert.That(context.Renderer.UnhandledException.IsCompleted).IsFalse();
    }
}
