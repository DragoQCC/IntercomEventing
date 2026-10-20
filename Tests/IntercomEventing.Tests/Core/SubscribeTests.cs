using IntercomEventing.Tests.TestSupport;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// <c>Subscribe</c>, <c>Unsubscribe</c> and the subscription handle: every removal route really removes the subscription,
/// by reference identity, exactly once.
/// </summary>
/// <remarks>
/// Removals racing each other on several threads are tested in <see cref="ConcurrencyTests"/>.
/// </remarks>
public class SubscribeTests
{
    [Test]
    public async Task Dispose_RemovesTheSubscriptionAndStopsItsCalls()
    {
        //S2: in 1.2.0 the record hash included _isDisposed, so disposing a handle never removed it from the event
        var evt = new IntercomEvent<int>(new EventingOptions());
        var calls = 0;
        var subscription = evt.Subscribe(_ =>
        {
            Interlocked.Increment(ref calls);
        });
        await evt.RaiseAsync(1);

        subscription.Dispose();
        await evt.RaiseAsync(2);

        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(1);
        await Assert.That(subscription.IsActive).IsFalse();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task DisposeAsync_RemovesTheSubscription()
    {
        //S2: the asynchronous route removes it too
        var evt = new IntercomEvent<int>(new EventingOptions());
        var subscription = evt.Subscribe(_ => Task.CompletedTask);

        await subscription.DisposeAsync();

        await Assert.That(subscription.IsActive).IsFalse();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task UnsubscribeAsync_RemovesTheSubscription()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var subscription = evt.Subscribe(_ => Task.CompletedTask);

        await subscription.UnsubscribeAsync();

        await Assert.That(subscription.IsActive).IsFalse();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task DisposedHandle_LetsAReplacementSubscribeToASingleSubscriberEvent()
    {
        //S2: in 1.2.0 the zombie entry made the replacement fail with "Multiple subscribers are not allowed"
        var evt = new IntercomEvent<int>(new EventingOptions { AllowMultipleSubscribers = false });
        var firstSubscription = evt.Subscribe(_ => Task.CompletedTask);
        firstSubscription.Dispose();

        var replacement = evt.Subscribe(_ => Task.CompletedTask);

        await Assert.That(replacement.IsActive).IsTrue();
        await Assert.That(evt.SubscriberCount).IsEqualTo(1);
    }

    [Test]
    public async Task RemovingTheLastSubscription_ReturnsRaisesToTheZeroSubscriberPath()
    {
        //S2: in 1.2.0 a disposed subscription kept the event off its "no subscribers" fast path; now no call is even built
        var evt = new ProbeEvent(new EventingOptions());
        var subscription = evt.Subscribe(_ => Task.CompletedTask);
        await evt.RaiseAsync(1);
        var factoryCallsWhileSubscribed = evt.FactoryInvocations;

        subscription.Dispose();
        await evt.RaiseAsync(2);

        await Assert.That(factoryCallsWhileSubscribed).IsEqualTo(1);
        await Assert.That(evt.FactoryInvocations).IsEqualTo(1);
    }

    [Test]
    public async Task HandleOfOneOfTwoEqualDelegates_RemovesOnlyItsOwnSubscription()
    {
        //S2/S3: subscriptions are found by reference identity, not by a value hash that equal delegates would share
        var evt = new IntercomEvent<int>(new EventingOptions());
        var routedTo = new List<string>();
        Func<EventCall<int>, Task> sharedHandler = _ => throw new InvalidOperationException("shared");
        var firstSubscription = evt.Subscribe(sharedHandler, (_, _) => routedTo.Add("first"));
        var secondSubscription = evt.Subscribe(sharedHandler, (_, _) => routedTo.Add("second"));

        firstSubscription.Dispose();
        await evt.RaiseAsync(1);

        await Assert.That(routedTo).IsEquivalentTo(["second"]);
        await Assert.That(firstSubscription.IsActive).IsFalse();
        await Assert.That(secondSubscription.IsActive).IsTrue();
    }

    [Test]
    public async Task DisposingAHandleAgain_RemovesNothingMore()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        Func<EventCall<int>, Task> sharedHandler = _ => Task.CompletedTask;
        var firstSubscription = evt.Subscribe(sharedHandler);
        evt.Subscribe(sharedHandler);

        firstSubscription.Dispose();
        firstSubscription.Dispose();
        await firstSubscription.DisposeAsync();

        await Assert.That(evt.SubscriberCount).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAndUnsubscribe_StillRemoveTheSubscriptionAfterTheEventChanged()
    {
        //A1: in 1.2.0 the subscription hash included the event's own hash, which changed with CurrentValue after an increment
        var threshold = new ThresholdEvent<int>(threshold: 1, options: new EventingOptions());
        Action<ThresholdEventCall<int, NoPayload>> handler = _ => { };
        var handleSubscription = threshold.Subscribe(_ => Task.CompletedTask);
        threshold.Subscribe(handler);
        await threshold.IncrementAsync(1);
        await threshold.IncrementAsync(5);

        handleSubscription.Dispose();
        var removedByUnsubscribe = threshold.Unsubscribe(handler);

        await Assert.That(removedByUnsubscribe).IsTrue();
        await Assert.That(threshold.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task Unsubscribe_ReportsWhetherASubscriptionWasRemoved()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        Func<EventCall<int>, Task> asyncHandler = _ => Task.CompletedTask;
        Action<EventCall<int>> syncHandler = _ => { };
        evt.Subscribe(asyncHandler);
        evt.Subscribe(syncHandler);

        var removedAsync = evt.Unsubscribe(asyncHandler);
        var removedSync = evt.Unsubscribe(syncHandler);
        var removedAgain = evt.Unsubscribe(asyncHandler);

        await Assert.That(removedAsync).IsTrue();
        await Assert.That(removedSync).IsTrue();
        await Assert.That(removedAgain).IsFalse();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task CombinedDelegateSubscribe_IsOneSubscriptionWhoseEveryPartRunsEvenAfterAThrow()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var routedFailures = new List<string>();
        var laterPartRan = false;
        Func<EventCall<int>, Task> failingPart = _ => throw new InvalidOperationException("first part");
        Func<EventCall<int>, Task> laterPart = _ =>
        {
            laterPartRan = true;
            return Task.CompletedTask;
        };

        var subscription = evt.Subscribe(failingPart + laterPart, (exception, _) => routedFailures.Add(exception.Message));
        await evt.RaiseAsync(1);

        await Assert.That(evt.SubscriberCount).IsEqualTo(1);
        await Assert.That(laterPartRan).IsTrue();
        await Assert.That(routedFailures).IsEquivalentTo(["first part"]);
        await Assert.That(subscription.IsActive).IsTrue();
    }

    [Test]
    public async Task RemovingWithTheSameCombinedDelegate_RemovesTheWholeSubscription()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        Func<EventCall<int>, Task> partA = _ => Task.CompletedTask;
        Func<EventCall<int>, Task> partB = _ => Task.CompletedTask;
        var combined = partA + partB;
        var removedByMinusEquals = evt.Subscribe(combined);
        var removedByUnsubscribe = evt.Subscribe(combined);

        evt.Raised -= combined;
        var unsubscribeRemovedOne = evt.Unsubscribe(combined);

        await Assert.That(removedByMinusEquals.IsActive).IsFalse();
        await Assert.That(removedByUnsubscribe.IsActive).IsFalse();
        await Assert.That(unsubscribeRemovedOne).IsTrue();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task NullHandlers_AreRejected()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());

        await Assert.That(() => evt.Subscribe((Func<EventCall<int>, Task>)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => evt.Subscribe((Action<EventCall<int>>)null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await evt.SubscribeAsync((Func<EventCall<int>, Task>)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => evt.Unsubscribe((Func<EventCall<int>, Task>)null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task SynchronousHandler_ReceivesEveryCall()
    {
        var evt = new IntercomEvent<string>(new EventingOptions());
        var received = new List<string>();
        using var subscription = evt.Subscribe(call => received.Add(call.Payload));

        await evt.RaiseAsync("first");
        await evt.RaiseAsync("second");

        await Assert.That(received).IsEquivalentTo(["first", "second"]);
    }

    [Test]
    public async Task HandleEvent_IsTheSubscribeOnlyViewOfItsEvent()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());

        var subscription = evt.Subscribe(_ => Task.CompletedTask);

        await Assert.That(subscription.Event).IsSameReferenceAs(evt);
        await Assert.That(subscription.Id).IsNotEqualTo(Guid.Empty);
    }

    [Test]
    public async Task HandlesOfDifferentEvents_CanBeStoredAndDisposedTogether()
    {
        //IEventSubscription drops the call type, so one list can own the subscriptions of a whole component
        var userCreated = new IntercomEvent<UserCreated>(new EventingOptions());
        var backlog = new ThresholdEvent<int>(threshold: 10, options: new EventingOptions());
        List<IEventSubscription> subscriptions =
        [
            userCreated.Subscribe(_ => { }),
            backlog.Subscribe(_ => { }),
        ];

        foreach (var subscription in subscriptions)
        {
            await subscription.DisposeAsync();
        }

        await Assert.That(subscriptions).All(subscription => !subscription.IsActive);
        await Assert.That(userCreated.SubscriberCount).IsEqualTo(0);
        await Assert.That(backlog.SubscriberCount).IsEqualTo(0);
    }
}
