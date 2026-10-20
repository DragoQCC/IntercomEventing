using System.Collections.Concurrent;
using IntercomEventing.Tests.TestSupport;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// The native <c>Raised</c> event: <c>+=</c> and <c>-=</c> on get-only properties and interface views, native removal
/// semantics, and combined delegates added all-or-nothing.
/// </summary>
public class RaisedEventAccessorTests
{
    [Test]
    public async Task PlusEquals_OnAGetOnlyProperty_Subscribes()
    {
        //sample section 1: Raised is an event member, so += compiles on a get-only property
        var registry = new UserRegistry();
        var received = new List<string>();
        registry.UserCreated.Raised += call =>
        {
            received.Add(call.Payload.Name);
            return Task.CompletedTask;
        };

        await registry.CreateAsync(new UserCreated("Ada", "ada@example.com"));

        await Assert.That(received).IsEquivalentTo(["Ada"]);
        await Assert.That(registry.UserCreated.SubscriberCount).IsEqualTo(1);
    }

    [Test]
    public async Task MinusEquals_WithTheSameMethodGroup_RemovesTheSubscription()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var recorder = new CallRecorder();
        evt.Raised += recorder.RecordAsync;

        evt.Raised -= recorder.RecordAsync;
        await evt.RaiseAsync(1);

        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
        await Assert.That(recorder.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MinusEquals_WithARewrittenLambda_RemovesNothing()
    {
        //native semantics: a lambda written again is a different delegate, so only the kept instance removes the subscription
        var evt = new IntercomEvent<int>(new EventingOptions());
        Func<EventCall<int>, Task> keptHandler = _ => Task.CompletedTask;
        evt.Raised += keptHandler;

        evt.Raised -= _ => Task.CompletedTask;
        var removedByRewrittenLambda = evt.Unsubscribe(_ => Task.CompletedTask);
        var countAfterRewrittenLambdas = evt.SubscriberCount;
        evt.Raised -= keptHandler;

        await Assert.That(removedByRewrittenLambda).IsFalse();
        await Assert.That(countAfterRewrittenLambdas).IsEqualTo(1);
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task MinusEquals_RemovesTheMostRecentEqualSubscription()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        Func<EventCall<int>, Task> sharedHandler = _ => Task.CompletedTask;
        var earlierSubscription = evt.Subscribe(sharedHandler);
        var laterSubscription = evt.Subscribe(sharedHandler);

        evt.Raised -= sharedHandler;

        await Assert.That(laterSubscription.IsActive).IsFalse();
        await Assert.That(earlierSubscription.IsActive).IsTrue();
        await Assert.That(evt.SubscriberCount).IsEqualTo(1);
    }

    [Test]
    public async Task PlusEqualsAndMinusEqualsNull_AreNoOps()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        Func<EventCall<int>, Task>? noHandler = null;

        evt.Raised += noHandler;
        evt.Raised -= noHandler;

        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task CombinedDelegate_BecomesOneSubscriptionPerPartWithItsOwnRouting()
    {
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions { DefaultExceptionHandler = failures.Record });
        Func<EventCall<int>, Task> failingPartOne = _ => throw new InvalidOperationException("one");
        Func<EventCall<int>, Task> failingPartTwo = _ => throw new InvalidOperationException("two");

        evt.Raised += failingPartOne + failingPartTwo;
        await evt.RaiseAsync(1);

        await Assert.That(evt.SubscriberCount).IsEqualTo(2);
        var failingSubscriptions = failures.Failures.Select(failure => failure.SubscriptionId).Distinct().ToArray();
        await Assert.That(failingSubscriptions).Count().IsEqualTo(2);
    }

    [Test]
    public async Task MinusEquals_WithACombinedDelegate_RemovesEveryPart()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        Func<EventCall<int>, Task> partA = _ => Task.CompletedTask;
        Func<EventCall<int>, Task> partB = _ => Task.CompletedTask;
        Func<EventCall<int>, Task> partC = _ => Task.CompletedTask;
        evt.Raised += partA + partB + partC;
        var countAfterAdding = evt.SubscriberCount;

        evt.Raised -= partA + partB + partC;

        await Assert.That(countAfterAdding).IsEqualTo(3);
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task CombinedDelegate_OnASingleSubscriberEvent_IsRejectedAndAddsNothing()
    {
        //all-or-nothing: the synthesis prototype left the earlier parts subscribed when a later part was rejected
        var evt = new IntercomEvent<int>(new EventingOptions { AllowMultipleSubscribers = false });
        Func<EventCall<int>, Task> partA = _ => Task.CompletedTask;
        Func<EventCall<int>, Task> partB = _ => Task.CompletedTask;
        var combined = partA + partB;

        await Assert.That(() => evt.Raised += combined).Throws<InvalidOperationException>();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task SecondSubscriber_OnASingleSubscriberEvent_IsRejectedAndTheFirstStays()
    {
        var evt = new IntercomEvent<int>(new EventingOptions { AllowMultipleSubscribers = false });
        Func<EventCall<int>, Task> firstHandler = _ => Task.CompletedTask;
        Func<EventCall<int>, Task> secondHandler = _ => Task.CompletedTask;
        evt.Raised += firstHandler;

        await Assert.That(() => evt.Raised += secondHandler).Throws<InvalidOperationException>();
        await Assert.That(() => evt.Subscribe(secondHandler)).Throws<InvalidOperationException>();
        await Assert.That(async () => await evt.SubscribeAsync(secondHandler)).Throws<InvalidOperationException>();
        await Assert.That(evt.SubscriberCount).IsEqualTo(1);
    }

    [Test]
    public async Task CombinedDelegate_AfterDisposal_ThrowsAndAddsNothing()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        await evt.DisposeAsync();
        Func<EventCall<int>, Task> partA = _ => Task.CompletedTask;
        Func<EventCall<int>, Task> partB = _ => Task.CompletedTask;
        var combined = partA + partB;

        await Assert.That(() => evt.Raised += combined).Throws<ObjectDisposedException>();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentCombinedSubscriptions_AreNeverSeenHalfAdded()
    {
        //a raise sees either every part of a combined delegate or none of them
        var evt = new IntercomEvent<int>(new EventingOptions());
        var partHitsPerRaise = new ConcurrentDictionary<Guid, int[]>();
        var triple = CreatePartCountingHandler(partHitsPerRaise, 0)
            + CreatePartCountingHandler(partHitsPerRaise, 1)
            + CreatePartCountingHandler(partHitsPerRaise, 2);
        var stopRaising = false;
        var raiser = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stopRaising))
            {
                await evt.RaiseAsync(1);
            }
        });

        Parallel.For(0, 2000, _ =>
        {
            evt.Raised += triple;
        });
        Volatile.Write(ref stopRaising, true);
        await raiser.WaitAsync(Eventually.DefaultTimeout);

        Func<int[], bool> partCountsDiffer = hits => hits[0] != hits[1] || hits[1] != hits[2];
        var tornRaises = partHitsPerRaise.Values.Count(partCountsDiffer);
        await Assert.That(tornRaises).IsEqualTo(0);
        await Assert.That(evt.SubscriberCount).IsEqualTo(6000);
    }

    [Test]
    public async Task ConcurrentPlusAndMinusEquals_KeepWholeCombinedSubscriptions()
    {
        //a raise already delivering can skip parts removed after it took its snapshot (documented), but only a trailing run:
        //parts are removed in one write and skipped only once removed, so per-part counts never increase along the parts.
        //Starting with as many triples as there are removals means every -= finds a triple to remove whatever the
        //interleaving, so the final count is exact: a lost or doubled update of a whole triple changes it
        const int initialTriples = 1000;
        const int concurrentOperations = 2 * initialTriples;
        var evt = new IntercomEvent<int>(new EventingOptions());
        var partHitsPerRaise = new ConcurrentDictionary<Guid, int[]>();
        var triple = CreatePartCountingHandler(partHitsPerRaise, 0)
            + CreatePartCountingHandler(partHitsPerRaise, 1)
            + CreatePartCountingHandler(partHitsPerRaise, 2);
        for (var tripleNumber = 0; tripleNumber < initialTriples; tripleNumber++)
        {
            evt.Raised += triple;
        }
        var stopRaising = false;
        var raiser = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stopRaising))
            {
                await evt.RaiseAsync(1);
            }
        });

        Parallel.For(0, concurrentOperations, index =>
        {
            if (index % 2 == 0)
            {
                evt.Raised += triple;
            }
            else
            {
                evt.Raised -= triple;
            }
        });
        Volatile.Write(ref stopRaising, true);
        await raiser.WaitAsync(Eventually.DefaultTimeout);

        Func<int[], bool> partCountsDiffer = hits => hits[0] != hits[1] || hits[1] != hits[2];
        Func<int[], bool> isASkippedTail = hits => hits[0] >= hits[1] && hits[1] >= hits[2];
        var tornRaises = partHitsPerRaise.Values.Where(partCountsDiffer).ToArray();
        var tornRaisesExplainedBySkippedTail = tornRaises.Count(isASkippedTail);
        //as many additions as removals ran, so the triples subscribed at the start are exactly what remains
        await Assert.That(evt.SubscriberCount).IsEqualTo(initialTriples * 3);
        await Assert.That(tornRaisesExplainedBySkippedTail).IsEqualTo(tornRaises.Length);
    }

    [Test]
    public async Task HandlerForTheBaseCallType_AttachesToEveryEventKind()
    {
        //EventCall is the base of every call type and Func<in T, TResult> is contravariant
        var calls = new ConcurrentQueue<EventCall>();
        Func<EventCall, Task> logAnyCall = call =>
        {
            calls.Enqueue(call);
            return Task.CompletedTask;
        };
        var userCreated = new IntercomEvent<UserCreated>(new EventingOptions());
        var backlog = new ThresholdEvent<int>(threshold: 1, options: new EventingOptions());
        userCreated.Raised += logAnyCall;
        backlog.Raised += logAnyCall;

        await userCreated.RaiseAsync(new UserCreated("Ada", "ada@example.com"));
        await backlog.IncrementAsync(1);

        var callTypes = calls.Select(call => call.GetType()).ToArray();
        await Assert.That(callTypes).IsEquivalentTo([typeof(EventCall<UserCreated>), typeof(ThresholdEventCall<int, NoPayload>)]);
    }

    [Test]
    public async Task SubscribeOnlyView_SupportsPlusAndMinusEquals()
    {
        var registry = new UserRegistry();
        var received = 0;
        Func<EventCall<string>, Task> onUserRemoved = _ =>
        {
            Interlocked.Increment(ref received);
            return Task.CompletedTask;
        };

        registry.UserRemoved.Raised += onUserRemoved;
        await registry.RemoveAsync("Ada");
        registry.UserRemoved.Raised -= onUserRemoved;
        await registry.RemoveAsync("Grace");

        await Assert.That(Volatile.Read(ref received)).IsEqualTo(1);
        await Assert.That(registry.UserRemoved.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task AggregatorForwardingThroughInterfaceViews_SeesEveryRaiserAndRemovesItself()
    {
        //sample section 11 (AvionRelay after migration): += on interface properties, forwarding with the raiser as caller
        var signalRMonitor = new TransportMonitor("SignalR");
        var grpcMonitor = new TransportMonitor("Grpc");
        var aggregator = new TransportMonitorAggregator([signalRMonitor, grpcMonitor]);
        var raisedBy = new List<string>();
        aggregator.ClientConnected.Subscribe(call =>
        {
            var monitor = (TransportMonitor)call.Metadata.Caller!;
            lock (raisedBy)
            {
                raisedBy.Add($"{call.Payload.ClientName} via {monitor.TransportName}");
            }
        });

        await signalRMonitor.RaiseClientConnectedAsync(new ClientConnected("dashboard", new Dictionary<string, string>()));
        await grpcMonitor.RaiseClientConnectedAsync(new ClientConnected("worker-7", new Dictionary<string, string>()));
        await aggregator.DisposeAsync();

        await Assert.That(raisedBy).IsEquivalentTo(["dashboard via SignalR", "worker-7 via Grpc"]);
        await Assert.That(signalRMonitor.ClientConnected.SubscriberCount).IsEqualTo(0);
        await Assert.That(grpcMonitor.ClientConnected.SubscriberCount).IsEqualTo(0);
    }

    //each part records one hit per raise; a raise that reached only some parts of a combined delegate shows differing counts
    private static Func<EventCall<int>, Task> CreatePartCountingHandler(ConcurrentDictionary<Guid, int[]> partHitsPerRaise, int partIndex)
    {
        return call =>
        {
            var hits = partHitsPerRaise.GetOrAdd(call.Metadata.EventCallId, _ => new int[3]);
            Interlocked.Increment(ref hits[partIndex]);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// A producer exposing one event as a get-only property and another as its subscribe-only view.
    /// </summary>
    private sealed class UserRegistry
    {
        private readonly IntercomEvent<string> _userRemoved = new(new EventingOptions());

        public IntercomEvent<UserCreated> UserCreated { get; } = new(new EventingOptions());

        public IIntercomEvent<EventCall<string>> UserRemoved => _userRemoved;

        public Task CreateAsync(UserCreated user)
        {
            return UserCreated.RaiseAsync(user, caller: this);
        }

        public Task RemoveAsync(string userName)
        {
            return _userRemoved.RaiseAsync(userName, caller: this);
        }
    }

    private sealed class CallRecorder
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task RecordAsync(EventCall<int> call)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }

    private interface ITransportMonitor
    {
        IIntercomEvent<EventCall<ClientConnected>> ClientConnected { get; }
    }

    private sealed class TransportMonitor : ITransportMonitor
    {
        private readonly ClientConnectedEvent _clientConnected = new(new EventingOptions());

        public string TransportName { get; }

        public IIntercomEvent<EventCall<ClientConnected>> ClientConnected => _clientConnected;

        public TransportMonitor(string transportName)
        {
            TransportName = transportName;
        }

        public Task RaiseClientConnectedAsync(ClientConnected client)
        {
            return _clientConnected.RaiseAsync(client, caller: this);
        }
    }

    //merges the events of every monitor into one event, so the UI subscribes once
    private sealed class TransportMonitorAggregator : IAsyncDisposable
    {
        private readonly IReadOnlyList<ITransportMonitor> _monitors;

        public ClientConnectedEvent ClientConnected { get; } = new(new EventingOptions());

        public TransportMonitorAggregator(IReadOnlyList<ITransportMonitor> monitors)
        {
            _monitors = monitors;
            foreach (var monitor in _monitors)
            {
                monitor.ClientConnected.Raised += OnClientConnected;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var monitor in _monitors)
            {
                monitor.ClientConnected.Raised -= OnClientConnected;
            }
            await ClientConnected.DisposeAsync();
        }

        private Task OnClientConnected(EventCall<ClientConnected> call)
        {
            return ClientConnected.RaiseAsync(call.Payload, caller: call.Metadata.Caller);
        }
    }
}
