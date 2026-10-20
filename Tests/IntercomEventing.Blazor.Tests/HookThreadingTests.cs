using IntercomEventing.Blazor.Tests.TestSupport;
using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests;

/// <summary>
/// Where the <see cref="SubscriptionOptions{TCall}"/> hooks of a <see cref="ComponentEventSubscriptions.SubscribeAsync{TCall}(IIntercomEvent{TCall}, Func{TCall, Task}, SubscriptionOptions{TCall}?, bool, CancellationToken)"/>
/// subscription run, on bUnit's real renderer dispatcher: <c>OnSubscribed</c> on the caller's context, <c>OnUnsubscribed</c>
/// where the removal happens.
/// </summary>
public class HookThreadingTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task OnUnsubscribed_RunsOnTheRenderer_WhenTheComponentsDisposalRemovesTheSubscription()
    {
        await using var context = BlazorTestContext.CreateInteractive();
        var dispatcher = context.Renderer.Dispatcher;
        var events = new ComponentEventSubscriptions(dispatcher.InvokeAsync, () => { });
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var hookThreads = await SubscribeRecordingHookThreadsAsync(dispatcher, events, source);

        await dispatcher.InvokeAsync(async () => await events.DisposeAsync()).WaitAsync(WaitTimeout);

        await Assert.That(hookThreads.SubscribedOnTheRenderer).IsTrue();
        await Assert.That(hookThreads.UnsubscribeHookRan).IsTrue();
        await Assert.That(hookThreads.UnsubscribedOnTheRenderer).IsTrue();
    }

    [Test]
    public async Task OnUnsubscribed_RunsOnTheDisposingThread_WhenTheProducerDisposesTheEventFirst()
    {
        //documented on SubscribeAsync: a singleton producer disposed at shutdown runs the hook off the renderer
        await using var context = BlazorTestContext.CreateInteractive();
        var dispatcher = context.Renderer.Dispatcher;
        var events = new ComponentEventSubscriptions(dispatcher.InvokeAsync, () => { });
        var source = new IntercomEvent<int>(TestEventOptions.WaitForEveryHandler);
        var hookThreads = await SubscribeRecordingHookThreadsAsync(dispatcher, events, source);

        await Task.Run(async () => await source.DisposeAsync()).WaitAsync(WaitTimeout);

        await Assert.That(hookThreads.SubscribedOnTheRenderer).IsTrue();
        await Assert.That(hookThreads.UnsubscribeHookRan).IsTrue();
        await Assert.That(hookThreads.UnsubscribedOnTheRenderer).IsFalse();
    }

    private static async Task<HookThreadRecord> SubscribeRecordingHookThreadsAsync(Dispatcher dispatcher, ComponentEventSubscriptions events,
        IntercomEvent<int> source)
    {
        var hookThreads = new HookThreadRecord();
        //subscribed from a lifecycle method, which runs on the renderer
        await dispatcher.InvokeAsync(async () =>
        {
            await events.SubscribeAsync(source, call => { }, new()
            {
                OnSubscribed = _ =>
                {
                    hookThreads.SubscribedOnTheRenderer = dispatcher.CheckAccess();
                    return Task.CompletedTask;
                },
                OnUnsubscribed = _ =>
                {
                    hookThreads.UnsubscribeHookRan = true;
                    hookThreads.UnsubscribedOnTheRenderer = dispatcher.CheckAccess();
                    return Task.CompletedTask;
                },
            });
        }).WaitAsync(WaitTimeout);
        return hookThreads;
    }

    //whether each hook ran on the renderer's dispatcher
    private sealed class HookThreadRecord
    {
        public bool SubscribedOnTheRenderer { get; set; }

        public bool UnsubscribeHookRan { get; set; }

        public bool UnsubscribedOnTheRenderer { get; set; }
    }
}
