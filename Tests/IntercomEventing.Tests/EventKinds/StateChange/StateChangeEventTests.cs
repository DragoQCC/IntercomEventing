using System.Collections.Concurrent;
using IntercomEventing.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace IntercomEventing.Tests.EventKinds.StateChange;

/// <summary>
/// <see cref="StateChangeEvent{TState,TPayload}"/>: raises only on a real change (by the supplied comparer, null-safe), and
/// <c>ChangeStateAsync</c> is awaitable and reports failures to its caller.
/// </summary>
public class StateChangeEventTests
{
    [Test]
    public async Task ChangeStateAsync_RaisesOnlyWhenTheStateActuallyChanges()
    {
        //sample section 5
        var powerChanged = new StateChangeEvent<PowerState, ServerInfo>(PowerState.Off, options: new EventingOptions());
        var transitions = new List<string>();
        powerChanged.Subscribe(call => transitions.Add($"{call.Payload.Name}: {call.OldState} -> {call.NewState}"));
        var webServer = new ServerInfo("web-01", "192.168.1.10");

        var started = await powerChanged.ChangeStateAsync(PowerState.On, webServer);
        var repeated = await powerChanged.ChangeStateAsync(PowerState.On, webServer);
        var restarting = await powerChanged.ChangeStateAsync(PowerState.Restarting, webServer);

        await Assert.That(started).IsTrue();
        await Assert.That(repeated).IsFalse();
        await Assert.That(restarting).IsTrue();
        await Assert.That(transitions).IsEquivalentTo(["web-01: Off -> On", "web-01: On -> Restarting"], CollectionOrdering.Matching);
        await Assert.That(powerChanged.CurrentState).IsEqualTo(PowerState.Restarting);
    }

    [Test]
    public async Task ChangeStateAsync_CompletesOnlyAfterTheHandlersFinished()
    {
        //S5: the 1.2.0 setter fired OnStateChanged without awaiting it (CS4014), so the caller could not wait for the handlers
        var connection = new StateChangeEvent<string>("disconnected", options: new EventingOptions { StartNextEventHandlerAfter = null });
        var handlerGate = Gate.Create();
        var handlerFinished = false;
        connection.Subscribe(async _ =>
        {
            await handlerGate.Task;
            Volatile.Write(ref handlerFinished, true);
        });

        var changing = connection.ChangeStateAsync("connected");
        var completedBeforeTheHandler = changing.IsCompleted;
        handlerGate.SetResult();
        var changed = await changing.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(completedBeforeTheHandler).IsFalse();
        await Assert.That(changed).IsTrue();
        await Assert.That(Volatile.Read(ref handlerFinished)).IsTrue();
    }

    [Test]
    public async Task NullStates_AreSupportedInBothDirections()
    {
        //S5: in 1.2.0 a null state threw NullReferenceException on every later assignment
        var region = new StateChangeEvent<string?>(initialState: null, options: new EventingOptions());
        var transitions = new List<string>();
        region.Subscribe(call => transitions.Add($"{call.OldState ?? "null"}->{call.NewState ?? "null"}"));

        var toValue = await region.ChangeStateAsync("A");
        var toNull = await region.ChangeStateAsync(null);
        var nullAgain = await region.ChangeStateAsync(null);
        var fromNullAgain = await region.ChangeStateAsync("B");

        await Assert.That(toValue).IsTrue();
        await Assert.That(toNull).IsTrue();
        await Assert.That(nullAgain).IsFalse();
        await Assert.That(fromNullAgain).IsTrue();
        await Assert.That(transitions).IsEquivalentTo(["null->A", "A->null", "null->B"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Comparer_DecidesWhetherTheStateChanged()
    {
        var region = new StateChangeEvent<string?>("eu", StringComparer.OrdinalIgnoreCase, new EventingOptions());
        var transitions = new List<string>();
        region.Subscribe(call => transitions.Add($"{call.OldState ?? "null"}->{call.NewState ?? "null"}"));

        var sameIgnoringCase = await region.ChangeStateAsync("EU");
        var stateAfterTheEqualValue = region.CurrentState;
        var toNull = await region.ChangeStateAsync(null);
        var nullToNull = await region.ChangeStateAsync(null);
        var fromNull = await region.ChangeStateAsync("us");

        await Assert.That(sameIgnoringCase).IsFalse();
        //an equal state is not stored, so the original casing survives
        await Assert.That(stateAfterTheEqualValue).IsEqualTo("eu");
        await Assert.That(toNull).IsTrue();
        await Assert.That(nullToNull).IsFalse();
        await Assert.That(fromNull).IsTrue();
        await Assert.That(transitions).IsEquivalentTo(["eu->null", "null->us"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Call_CarriesTheOldAndNewStateThePayloadAndTheCaller()
    {
        var powerChanged = new StateChangeEvent<PowerState, ServerInfo>(PowerState.Off, options: new EventingOptions());
        StateChangeEventCall<PowerState, ServerInfo>? receivedCall = null;
        powerChanged.Subscribe(call =>
        {
            receivedCall = call;
        });
        var webServer = new ServerInfo("web-01", "192.168.1.10");
        var monitor = new object();

        await powerChanged.ChangeStateAsync(PowerState.Starting, webServer, caller: monitor);

        await Assert.That(receivedCall).IsNotNull();
        await Assert.That(receivedCall!.OldState).IsEqualTo(PowerState.Off);
        await Assert.That(receivedCall.NewState).IsEqualTo(PowerState.Starting);
        await Assert.That(receivedCall.Payload).IsSameReferenceAs(webServer);
        await Assert.That(receivedCall.Metadata.Caller).IsSameReferenceAs(monitor);
    }

    [Test]
    public async Task AsyncHandlerFault_IsRouted()
    {
        //S5c: an async handler fault was routed 0 times in 1.2.0. The handler timeout runs on a clock that never moves, so the
        //yielding handler always finishes before ChangeStateAsync returns
        var failures = new FailureRecorder();
        var connection = new StateChangeEvent<int>(0, options: new EventingOptions { DefaultExceptionHandler = failures.Record, TimeProvider = new FakeTimeProvider() });
        connection.Subscribe(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("state handler failure");
        });

        await connection.ChangeStateAsync(1);

        await Assert.That(failures.Count).IsEqualTo(1);
        await Assert.That(failures.Failures[0].Call).IsTypeOf<StateChangeEventCall<int, NoPayload>>();
    }

    [Test]
    public async Task RaiseFailure_ReachesTheCaller()
    {
        //S5: a failing CreateEventCall was swallowed by the 1.2.0 setter; a failing raise now faults ChangeStateAsync
        var clock = new ThrowingClock(new FakeTimeProvider());
        var connection = new StateChangeEvent<int>(0, options: new EventingOptions { TimeProvider = clock });
        connection.Subscribe(_ => { });
        clock.ThrowOnGetUtcNow = true;

        var thrown = await Assert.That(async () => await connection.ChangeStateAsync(1)).Throws<InvalidOperationException>();

        await Assert.That(thrown!.Message).IsEqualTo("clock failure");
    }

    [Test]
    public async Task ConcurrentChanges_FormOneConsistentChain()
    {
        //every call has its own consistent old/new pair, so the reported transitions link up into one chain
        var state = new StateChangeEvent<int>(-1, options: new EventingOptions());
        var transitions = new ConcurrentDictionary<int, int>();
        var oldStatesReportedTwice = 0;
        state.Subscribe(call =>
        {
            if (!transitions.TryAdd(call.OldState, call.NewState))
            {
                Interlocked.Increment(ref oldStatesReportedTwice);
            }
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 300).Select(value => Task.Run(() => state.ChangeStateAsync(value))))
            .WaitAsync(Eventually.DefaultTimeout);
        var chainLength = 0;
        var chainEnd = -1;
        while (transitions.TryGetValue(chainEnd, out var nextState))
        {
            chainEnd = nextState;
            chainLength++;
        }

        await Assert.That(results).All(changed => changed);
        await Assert.That(Volatile.Read(ref oldStatesReportedTwice)).IsEqualTo(0);
        await Assert.That(chainLength).IsEqualTo(300);
        await Assert.That(chainEnd).IsEqualTo(state.CurrentState);
    }

    [Test]
    public async Task CancellationWhileHandlersStart_LeavesTheNewStateApplied()
    {
        //documented: only a token cancelled before the call is reported before the state changes
        var state = new StateChangeEvent<int>(0, options: new EventingOptions { DispatchMode = DispatchMode.Sequential });
        using var raiseCancellation = new CancellationTokenSource();
        var secondHandlerRan = false;
        state.Subscribe(_ => raiseCancellation.Cancel());
        state.Subscribe(_ =>
        {
            secondHandlerRan = true;
        });

        await Assert.That(async () => await state.ChangeStateAsync(1, cancellationToken: raiseCancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(state.CurrentState).IsEqualTo(1);
        await Assert.That(secondHandlerRan).IsFalse();
    }

    [Test]
    public async Task ChangeAfterDisposal_ThrowsAndLeavesTheState()
    {
        //A6
        var state = new StateChangeEvent<int>(0, options: new EventingOptions());
        await state.DisposeAsync();

        await Assert.That(async () => await state.ChangeStateAsync(1)).Throws<ObjectDisposedException>();
        await Assert.That(state.CurrentState).IsEqualTo(0);
    }

    [Test]
    public async Task ChangeWithACancelledToken_ThrowsAndLeavesTheState()
    {
        var state = new StateChangeEvent<int>(0, options: new EventingOptions());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.That(async () => await state.ChangeStateAsync(1, cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(state.CurrentState).IsEqualTo(0);
    }
}
