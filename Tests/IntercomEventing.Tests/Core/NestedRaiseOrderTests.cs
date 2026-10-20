using System.Collections.Concurrent;
using TUnit.Assertions.Enums;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// A raise made from inside a handler is delivered inline, so it can reach later subscribers before the raise that
/// triggered it (README "Things to Watch Out For").
/// </summary>
public class NestedRaiseOrderTests
{
    [Test]
    [Arguments(DispatchMode.Parallel)]
    [Arguments(DispatchMode.Sequential)]
    public async Task ReplyRaisedBeforeTheResponderAwaits_ReachesLaterSubscribersFirst(DispatchMode dispatchMode)
    {
        var deliveries = new ConcurrentQueue<string>();
        await using var messagePosted = new IntercomEvent<string>(new EventingOptions { DispatchMode = dispatchMode });
        messagePosted.Subscribe(async call =>
        {
            if (call.Payload == "question")
            {
                await messagePosted.RaiseAsync("reply");
            }
        });
        messagePosted.Subscribe(call => deliveries.Enqueue(call.Payload));

        await messagePosted.RaiseAsync("question");

        await Assert.That(deliveries).IsEquivalentTo(["reply", "question"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(DispatchMode.Parallel, new[] { "question", "reply" })]
    [Arguments(DispatchMode.Sequential, new[] { "reply", "question" })]
    public async Task ReplyRaisedAfterTheResponderAwaited_DependsOnTheDispatchMode(DispatchMode dispatchMode, string[] expectedOrder)
    {
        //Parallel starts the next handler once the responder awaits; Sequential waits for the responder, reply included
        var deliveries = new ConcurrentQueue<string>();
        var responderAwaited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var messagePosted = new IntercomEvent<string>(new EventingOptions { DispatchMode = dispatchMode });
        messagePosted.Subscribe(async call =>
        {
            if (call.Payload == "question")
            {
                await responderAwaited.Task;
                await messagePosted.RaiseAsync("reply");
            }
        });
        messagePosted.Subscribe(call => deliveries.Enqueue(call.Payload));

        var outerRaise = messagePosted.RaiseAsync("question");
        responderAwaited.SetResult();
        await outerRaise;

        await Assert.That(deliveries).IsEquivalentTo(expectedOrder, CollectionOrdering.Matching);
    }
}
