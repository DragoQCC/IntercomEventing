using Bunit;
using IntercomEventing.Blazor.Tests.Components;
using IntercomEventing.Blazor.Tests.TestSupport;

namespace IntercomEventing.Blazor.Tests;

/// <summary>
/// Handler exceptions of component subscriptions: the subscription's own handler, else the nearest ErrorBoundary (through
/// DispatchExceptionAsync), else unhandled by the renderer.
/// </summary>
public class ExceptionRoutingTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task HandlerException_IsShownByTheNearestErrorBoundary()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var source = new IntercomEvent<string>(TestEventOptions.WaitForEveryHandler);
        var guarded = context.Render<GuardedFailingSubscriber>(parameters => parameters.Add(component => component.Source, source));

        await Task.Run(() => source.RaiseAsync("order 7"));
        await guarded.WaitForStateAsync(() => guarded.FindAll(".boundary").Count == 1, WaitTimeout);

        await Assert.That(guarded.Find(".boundary").TextContent).IsEqualTo("boundary: handler failed for order 7");
        //the boundary disposed the failed component, which removed its subscription
        await Assert.That(source.SubscriberCount).IsEqualTo(0);
        await Assert.That(context.Renderer.UnhandledException.IsCompleted).IsFalse();
    }

    [Test]
    public async Task HandlerException_WithTheSubscriptionsOwnHandler_StaysInTheComponent()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var source = new IntercomEvent<string>(TestEventOptions.WaitForEveryHandler);
        var guarded = context.Render<GuardedFailingSubscriber>(parameters => parameters
            .Add(component => component.Source, source)
            .Add(component => component.HandleInComponent, true));

        await Task.Run(() => source.RaiseAsync("order 8"));
        await guarded.WaitForStateAsync(() => guarded.Find(".handled").TextContent != "nothing handled", WaitTimeout);

        await Assert.That(guarded.Find(".handled").TextContent).IsEqualTo("handled: handler failed for order 8");
        await Assert.That(guarded.FindAll(".boundary").Count).IsEqualTo(0);
        await Assert.That(source.SubscriberCount).IsEqualTo(1);
    }

    [Test]
    public async Task ListenerCallbackException_IsShownByTheNearestErrorBoundary()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var source = new IntercomEvent<string>(TestEventOptions.WaitForEveryHandler);
        var guarded = context.Render<GuardedListener>(parameters => parameters.Add(component => component.Source, source));

        await Task.Run(() => source.RaiseAsync("order 9"));
        await guarded.WaitForStateAsync(() => guarded.FindAll(".boundary").Count == 1, WaitTimeout);

        await Assert.That(guarded.Find(".boundary").TextContent).IsEqualTo("boundary: callback failed for order 9");
        await Assert.That(guarded.FindAll(".content").Count).IsEqualTo(0);
        await Assert.That(source.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task ListenerGivenADisposedEvent_FailsTheParameterUpdate_AndTheErrorBoundaryShowsIt()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var liveEvent = new IntercomEvent<string>(TestEventOptions.WaitForEveryHandler);
        var disposedEvent = new IntercomEvent<string>(TestEventOptions.WaitForEveryHandler);
        await disposedEvent.DisposeAsync();
        var guarded = context.Render<GuardedListener>(parameters => parameters.Add(component => component.Source, liveEvent));

        guarded.Render(parameters => parameters.Add(component => component.Source, disposedEvent));

        await Assert.That(guarded.Find(".boundary-type").TextContent).IsEqualTo(nameof(ObjectDisposedException));
        await Assert.That(guarded.FindAll(".content").Count).IsEqualTo(0);
        //the old subscription is removed before the new event is subscribed to, so nothing is left on the live event
        await Assert.That(liveEvent.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandlerException_WithoutAnErrorBoundary_IsUnhandledByTheRenderer()
    {
        //the renderer's unhandled-exception path is what ends a Blazor Server circuit
        await using var context = BlazorTestContext.CreateInteractive();
        var source = new IntercomEvent<string>(TestEventOptions.WaitForEveryHandler);
        context.Render<FailingSubscriber>(parameters => parameters.Add(component => component.Source, source));

        await Task.Run(() => source.RaiseAsync("order 10"));
        var unhandledException = await context.Renderer.UnhandledException.WaitAsync(WaitTimeout);

        await Assert.That(unhandledException).IsTypeOf<InvalidOperationException>();
        await Assert.That(unhandledException.Message).IsEqualTo("handler failed for order 10");
    }
}
