using System.Collections.Concurrent;
using IntercomEventing.Tests.TestSupport;

namespace IntercomEventing.Tests.EventKinds.Generic;

/// <summary>
/// <see cref="IntercomEvent{TPayload}"/> and the payload-less <see cref="IntercomEvent"/>: the payload travels in a per-raise
/// call, so concurrent raises can never mix their data, and the raise task covers the handlers.
/// </summary>
public class IntercomEventTests
{
    [Test]
    public async Task RaiseAsync_DeliversThePayloadToEveryHandler()
    {
        var evt = new IntercomEvent<UserCreated>(new EventingOptions());
        var received = new ConcurrentQueue<UserCreated>();
        for (var handlerNumber = 0; handlerNumber < 3; handlerNumber++)
        {
            evt.Subscribe(call => received.Enqueue(call.Payload));
        }
        var ada = new UserCreated("Ada", "ada@example.com");

        await evt.RaiseAsync(ada);

        await Assert.That(received.Count).IsEqualTo(3);
        await Assert.That(received).All(user => ReferenceEquals(user, ada));
    }

    [Test]
    public async Task RaiseAsync_CompletesOnlyAfterTheHandlersFinished()
    {
        //A12: 1.2.0 samples raised from void methods without awaiting, so failures and timeouts were unobservable
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = null });
        var handlerGate = Gate.Create();
        var handlerFinished = false;
        evt.Subscribe(async _ =>
        {
            await handlerGate.Task;
            Volatile.Write(ref handlerFinished, true);
        });

        var raising = evt.RaiseAsync(1);
        var completedBeforeTheHandler = raising.IsCompleted;
        handlerGate.SetResult();
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(completedBeforeTheHandler).IsFalse();
        await Assert.That(Volatile.Read(ref handlerFinished)).IsTrue();
    }

    [Test]
    public async Task RaiseAsync_WithoutSubscribers_CompletesSynchronously()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());

        var raising = evt.RaiseAsync(1);

        await Assert.That(raising.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task PayloadlessEvent_DeliversNoPayload()
    {
        var cacheCleared = new IntercomEvent(new EventingOptions());
        EventCall<NoPayload>? receivedCall = null;
        cacheCleared.Subscribe(call =>
        {
            receivedCall = call;
        });
        var raiser = new object();

        await cacheCleared.RaiseAsync(caller: raiser);

        await Assert.That(receivedCall).IsNotNull();
        await Assert.That(receivedCall!.Payload).IsEqualTo(NoPayload.Value);
        await Assert.That(receivedCall.Payload.ToString()).IsEqualTo("(no payload)");
        await Assert.That(receivedCall.Metadata.Caller).IsSameReferenceAs(raiser);
    }

    [Test]
    public async Task NamedSubclass_KeepsItsNameAndTheGenericCallType()
    {
        var evt = new ClientConnectedEvent(new EventingOptions());
        EventCall<ClientConnected>? receivedCall = null;
        evt.Subscribe(call =>
        {
            receivedCall = call;
        });

        await evt.RaiseAsync(new ClientConnected("dashboard", new Dictionary<string, string>()));

        await Assert.That(receivedCall).IsNotNull();
        await Assert.That(receivedCall!.Metadata.EventName).IsEqualTo("ClientConnectedEvent");
        await Assert.That(receivedCall.Payload.ClientName).IsEqualTo("dashboard");
    }

    [Test]
    public async Task ConcurrentRaises_EachDeliverTheirOwnPayloadAndCaller()
    {
        //S10 (sample section 3): the 1.2.0 private-field pattern tore 94,090 of 200,000 concurrent calls and lost 99,206 clients
        const int numberOfRaises = 1000;
        const int numberOfHandlers = 3;
        var evt = new IntercomEvent<OrderPlaced>(new EventingOptions { DispatchMode = DispatchMode.Parallel, StartNextEventHandlerAfter = null });
        var receivedCalls = new ConcurrentBag<(int HandlerNumber, EventCall<OrderPlaced> Call)>();
        var inconsistentCalls = 0;
        for (var handlerNumber = 1; handlerNumber <= numberOfHandlers; handlerNumber++)
        {
            var thisHandlerNumber = handlerNumber;
            evt.Subscribe(async call =>
            {
                //yield so handlers of different raises interleave on the thread pool
                await Task.Yield();
                if (!DescribesOneOrder(call))
                {
                    Interlocked.Increment(ref inconsistentCalls);
                }
                receivedCalls.Add((thisHandlerNumber, call));
            });
        }

        var raises = Enumerable.Range(1, numberOfRaises)
            .Select(orderNumber => Task.Run(() => evt.RaiseAsync(
                new OrderPlaced(orderNumber, $"customer-{orderNumber}", orderNumber * 1.25m),
                caller: $"raiser-{orderNumber}")))
            .ToArray();
        await Task.WhenAll(raises).WaitAsync(Eventually.DefaultTimeout);

        var distinctOrdersPerHandler = receivedCalls
            .GroupBy(received => received.HandlerNumber)
            .Select(handlerCalls => handlerCalls.Select(received => received.Call.Payload.OrderNumber).Distinct().Count())
            .ToArray();
        var distinctCallIds = receivedCalls.Select(received => received.Call.Metadata.EventCallId).Distinct().Count();
        Func<IGrouping<int, (int HandlerNumber, EventCall<OrderPlaced> Call)>, bool> handlersOfTheRaiseSharedOneCall = raiseCalls =>
            raiseCalls.Select(received => received.Call).Distinct(ReferenceEqualityComparer.Instance).Count() == 1;
        var raisesSharingOneCall = receivedCalls.GroupBy(received => received.Call.Payload.OrderNumber).Count(handlersOfTheRaiseSharedOneCall);
        await Assert.That(Volatile.Read(ref inconsistentCalls)).IsEqualTo(0);
        await Assert.That(receivedCalls.Count).IsEqualTo(numberOfRaises * numberOfHandlers);
        await Assert.That(distinctOrdersPerHandler).All(distinctOrders => distinctOrders == numberOfRaises);
        await Assert.That(distinctCallIds).IsEqualTo(numberOfRaises);
        await Assert.That(raisesSharingOneCall).IsEqualTo(numberOfRaises);
    }

    [Test]
    public async Task CancelledRaiserToken_IsReportedThroughTheReturnedTask()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        evt.Subscribe(_ => { });
        using var raiseCancellation = new CancellationTokenSource();
        await raiseCancellation.CancelAsync();

        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);

        await Assert.That(async () => await raising).Throws<OperationCanceledException>();
    }

    //payload, caller and the derived fields must all describe the same order
    private static bool DescribesOneOrder(EventCall<OrderPlaced> call)
    {
        var order = call.Payload;
        var customerMatchesTheOrder = order.Customer == $"customer-{order.OrderNumber}";
        var totalMatchesTheOrder = order.Total == order.OrderNumber * 1.25m;
        var callerMatchesTheOrder = call.Metadata.Caller as string == $"raiser-{order.OrderNumber}";
        return customerMatchesTheOrder && totalMatchesTheOrder && callerMatchesTheOrder;
    }
}
