using IntercomEventing.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// How one raise runs its handlers: <see cref="DispatchMode"/>, <see cref="EventingOptions.MaxConcurrentHandlers"/> and
/// <see cref="EventingOptions.StartNextEventHandlerAfter"/>. Timeouts run on a <see cref="FakeTimeProvider"/>, so a handler
/// moves to the background exactly when the test advances the clock.
/// </summary>
public class DispatchModeTests
{
    //the dispatcher waits StartNextEventHandlerAfter plus this documented grace before moving a handler to the background
    private static readonly TimeSpan DocumentedTimeoutGrace = TimeSpan.FromMilliseconds(50);

    [Test]
    public async Task Parallel_HandlersThatFitTheLimit_AllStartInlineOnTheRaisingThread()
    {
        //A4 / decision D15: handlers start inline, so the synchronous part before their first await runs on the raiser's thread
        var evt = new IntercomEvent<int>(new EventingOptions { DispatchMode = DispatchMode.Parallel, StartNextEventHandlerAfter = null });
        var handlerGate = Gate.Create();
        var startThreads = new List<int>();
        for (var handlerNumber = 0; handlerNumber < 3; handlerNumber++)
        {
            evt.Subscribe(async _ =>
            {
                lock (startThreads)
                {
                    startThreads.Add(Environment.CurrentManagedThreadId);
                }
                await handlerGate.Task;
            });
        }

        var raisingThread = Environment.CurrentManagedThreadId;
        var raising = evt.RaiseAsync(1);
        int[] threadsSeenWhenRaiseReturned;
        lock (startThreads)
        {
            threadsSeenWhenRaiseReturned = startThreads.ToArray();
        }
        handlerGate.SetResult();
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(threadsSeenWhenRaiseReturned).Count().IsEqualTo(3);
        await Assert.That(threadsSeenWhenRaiseReturned).All(threadId => threadId == raisingThread);
    }

    [Test]
    public async Task Parallel_AllHandlersOfOneRaiseRunAtTheSameTime()
    {
        var evt = new IntercomEvent<int>(new EventingOptions { DispatchMode = DispatchMode.Parallel, StartNextEventHandlerAfter = null });
        var meter = new ConcurrencyMeter();
        var handlerGate = Gate.Create();
        for (var handlerNumber = 0; handlerNumber < 4; handlerNumber++)
        {
            evt.Subscribe(async _ =>
            {
                meter.Enter();
                await handlerGate.Task;
                meter.Exit();
            });
        }

        var raising = evt.RaiseAsync(1);
        var runningTogether = meter.Running;
        handlerGate.SetResult();
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(runningTogether).IsEqualTo(4);
        await Assert.That(meter.Running).IsEqualTo(0);
    }

    [Test]
    public async Task Sequential_RunsHandlersOneAfterAnotherInSubscriptionOrder()
    {
        //S4: MaxConcurrentHandlers is ignored in sequential mode, so even the default of 100 runs one handler at a time
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            DispatchMode = DispatchMode.Sequential,
            MaxConcurrentHandlers = 100,
            StartNextEventHandlerAfter = null,
        });
        var meter = new ConcurrencyMeter();
        var completionOrder = new List<int>();
        for (var handlerNumber = 1; handlerNumber <= 5; handlerNumber++)
        {
            var thisHandlerNumber = handlerNumber;
            evt.Subscribe(async _ =>
            {
                meter.Enter();
                await Task.Yield();
                lock (completionOrder)
                {
                    completionOrder.Add(thisHandlerNumber);
                }
                meter.Exit();
            });
        }

        await evt.RaiseAsync(1).WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(completionOrder).IsEquivalentTo([1, 2, 3, 4, 5], CollectionOrdering.Matching);
        await Assert.That(meter.Peak).IsEqualTo(1);
    }

    [Test]
    public async Task Sequential_HandlersThatFinishSynchronously_AllRunInlineOnTheRaisingThread()
    {
        var evt = new IntercomEvent<int>(new EventingOptions { DispatchMode = DispatchMode.Sequential });
        var handlerThreads = new List<int>();
        for (var handlerNumber = 0; handlerNumber < 3; handlerNumber++)
        {
            evt.Subscribe(_ => handlerThreads.Add(Environment.CurrentManagedThreadId));
        }

        var raisingThread = Environment.CurrentManagedThreadId;
        var raising = evt.RaiseAsync(1);

        await Assert.That(raising.IsCompletedSuccessfully).IsTrue();
        await Assert.That(handlerThreads).Count().IsEqualTo(3);
        await Assert.That(handlerThreads).All(threadId => threadId == raisingThread);
    }

    [Test]
    public async Task Sequential_NextHandlerStartsOnlyWhenThePreviousOneFinished()
    {
        var evt = new IntercomEvent<int>(new EventingOptions { DispatchMode = DispatchMode.Sequential, StartNextEventHandlerAfter = null });
        var firstHandlerGate = Gate.Create();
        var secondHandlerStarted = false;
        evt.Subscribe(async _ => await firstHandlerGate.Task);
        evt.Subscribe(_ =>
        {
            Volatile.Write(ref secondHandlerStarted, true);
        });

        var raising = evt.RaiseAsync(1);
        var secondStartedWhileFirstRan = Volatile.Read(ref secondHandlerStarted);
        firstHandlerGate.SetResult();
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(secondStartedWhileFirstRan).IsFalse();
        await Assert.That(Volatile.Read(ref secondHandlerStarted)).IsTrue();
    }

    [Test]
    public async Task Parallel_MaxConcurrentHandlers_NeverLetsMoreHandlersRunAtOnce()
    {
        //sample section 9: five handlers with a limit of two
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            DispatchMode = DispatchMode.Parallel,
            MaxConcurrentHandlers = 2,
            StartNextEventHandlerAfter = null,
        });
        var meter = new ConcurrencyMeter();
        var handlerGates = Enumerable.Range(0, 5).Select(_ => Gate.Create()).ToArray();
        foreach (var handlerGate in handlerGates)
        {
            evt.Subscribe(async _ =>
            {
                meter.Enter();
                await handlerGate.Task;
                meter.Exit();
            });
        }

        var raising = evt.RaiseAsync(1);
        var startedBeforeAnyFinished = meter.Entered;
        for (var index = 0; index < handlerGates.Length; index++)
        {
            handlerGates[index].SetResult();
            //each finished handler frees one slot for the next waiting handler
            var expectedStarted = Math.Min(index + 3, handlerGates.Length);
            var nextHandlerStarted = await Eventually.IsTrueAsync(() => meter.Entered == expectedStarted);
            await Assert.That(nextHandlerStarted).IsTrue();
        }
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(startedBeforeAnyFinished).IsEqualTo(2);
        await Assert.That(meter.Peak).IsEqualTo(2);
        await Assert.That(meter.Entered).IsEqualTo(5);
    }

    [Test]
    public async Task Parallel_MaxConcurrentHandlers_HoldsUnderManyYieldingHandlers()
    {
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            DispatchMode = DispatchMode.Parallel,
            MaxConcurrentHandlers = 4,
            StartNextEventHandlerAfter = null,
        });
        var meter = new ConcurrencyMeter();
        for (var handlerNumber = 0; handlerNumber < 50; handlerNumber++)
        {
            evt.Subscribe(async _ =>
            {
                meter.Enter();
                await Task.Yield();
                await Task.Yield();
                meter.Exit();
            });
        }

        for (var raiseNumber = 0; raiseNumber < 20; raiseNumber++)
        {
            await evt.RaiseAsync(raiseNumber).WaitAsync(Eventually.DefaultTimeout);
        }

        await Assert.That(meter.Entered).IsEqualTo(1000);
        await Assert.That(meter.Peak).IsLessThanOrEqualTo(4);
    }

    [Test]
    public async Task Parallel_HandlerMovedToTheBackground_FreesItsSlotForTheNextHandler()
    {
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(100);
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            DispatchMode = DispatchMode.Parallel,
            MaxConcurrentHandlers = 1,
            StartNextEventHandlerAfter = handlerTimeout,
            TimeProvider = clock,
        });
        var slowHandlerGate = Gate.Create();
        var secondHandlerStarted = false;
        evt.Subscribe(async _ => await slowHandlerGate.Task);
        evt.Subscribe(_ =>
        {
            Volatile.Write(ref secondHandlerStarted, true);
        });

        var raising = evt.RaiseAsync(1);
        var secondStartedBeforeTheTimeout = Volatile.Read(ref secondHandlerStarted);
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(secondStartedBeforeTheTimeout).IsFalse();
        await Assert.That(Volatile.Read(ref secondHandlerStarted)).IsTrue();
        await Assert.That(evt.BackgroundWorkCount).IsEqualTo(1);
        slowHandlerGate.SetResult();
        await evt.DisposeAsync().AsTask().WaitAsync(Eventually.DefaultTimeout);
    }

    [Test]
    public async Task Sequential_SlowHandler_KeepsRunningInTheBackgroundAfterStartNextEventHandlerAfter()
    {
        //sample section 8: the fast handler starts once the slow one exceeded 200 ms, and disposal waits for the slow one
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(200);
        var evt = new IntercomEvent<string>(new EventingOptions
        {
            DispatchMode = DispatchMode.Sequential,
            StartNextEventHandlerAfter = handlerTimeout,
            TimeProvider = clock,
        });
        var slowHandlerGate = Gate.Create();
        var slowHandlerFinished = false;
        var fastHandlerStarted = false;
        evt.Subscribe(async _ =>
        {
            await slowHandlerGate.Task;
            Volatile.Write(ref slowHandlerFinished, true);
        });
        evt.Subscribe(_ =>
        {
            Volatile.Write(ref fastHandlerStarted, true);
        });

        var raising = evt.RaiseAsync("Q3 report");
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace - TimeSpan.FromMilliseconds(1));
        var fastStartedJustBeforeTheTimeout = Volatile.Read(ref fastHandlerStarted);
        var raiseDoneJustBeforeTheTimeout = raising.IsCompleted;
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await raising.WaitAsync(Eventually.DefaultTimeout);
        var backgroundWorkAfterTheRaise = evt.BackgroundWorkCount;
        var disposing = evt.DisposeAsync().AsTask();
        var disposeWaitedForTheSlowHandler = !disposing.IsCompleted;
        slowHandlerGate.SetResult();
        await disposing.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(fastStartedJustBeforeTheTimeout).IsFalse();
        await Assert.That(raiseDoneJustBeforeTheTimeout).IsFalse();
        await Assert.That(Volatile.Read(ref fastHandlerStarted)).IsTrue();
        await Assert.That(backgroundWorkAfterTheRaise).IsEqualTo(1);
        await Assert.That(disposeWaitedForTheSlowHandler).IsTrue();
        await Assert.That(Volatile.Read(ref slowHandlerFinished)).IsTrue();
    }

    [Test]
    public async Task HandlerFinishingWithinTheGraceAfterTheTimeout_IsNotMovedToTheBackground()
    {
        //S11 (refuted in 1.2.0, behaviour kept): the 50 ms grace means a handler that takes the timeout, or slightly longer,
        //is still awaited. This handler takes 25 ms longer than the timeout; the clock first stops 1 ms past the timeout,
        //where a dispatcher without the grace would already have moved it to the background and returned
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(200);
        var handlerDuration = handlerTimeout + TimeSpan.FromMilliseconds(25);
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = handlerTimeout, TimeProvider = clock });
        evt.Subscribe(async _ => await Task.Delay(handlerDuration, clock, CancellationToken.None));

        var raising = evt.RaiseAsync(1);
        clock.Advance(handlerTimeout + TimeSpan.FromMilliseconds(1));
        var raiseStillWaitsJustPastTheTimeout = await Eventually.StaysIncompleteAsync(raising);
        clock.Advance(handlerDuration - handlerTimeout - TimeSpan.FromMilliseconds(1));
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(raiseStillWaitsJustPastTheTimeout).IsTrue();
        await Assert.That(evt.BackgroundWorkCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandlerSlowerThanTheTimeout_LetsTheNextRaiseStartAfterTheTimeout()
    {
        //S11 scenario: one subscriber with a slow handler and two back-to-back raises; the second starts one timeout later
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromSeconds(1);
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = handlerTimeout, TimeProvider = clock });
        var handlerGate = Gate.Create();
        var startedCalls = new List<int>();
        evt.Subscribe(async call =>
        {
            lock (startedCalls)
            {
                startedCalls.Add(call.Payload);
            }
            await handlerGate.Task;
        });

        var firstRaise = evt.RaiseAsync(1);
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await firstRaise.WaitAsync(Eventually.DefaultTimeout);
        var secondRaise = evt.RaiseAsync(2);
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await secondRaise.WaitAsync(Eventually.DefaultTimeout);
        int[] callsStarted;
        lock (startedCalls)
        {
            callsStarted = startedCalls.ToArray();
        }

        await Assert.That(callsStarted).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
        await Assert.That(evt.BackgroundWorkCount).IsEqualTo(2);
        handlerGate.SetResult();
        await evt.DisposeAsync().AsTask().WaitAsync(Eventually.DefaultTimeout);
    }

    [Test]
    public async Task SynchronousWorkBeforeTheFirstAwait_IsNotInterruptedByTheTimeout()
    {
        //A4 (open by decision D15, documented): the timeout bounds only the asynchronous part of a handler
        var clock = new FakeTimeProvider();
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = TimeSpan.Zero, TimeProvider = clock });
        using var handlerIsBlocking = new ManualResetEventSlim();
        using var releaseHandler = new ManualResetEventSlim();
        evt.Subscribe(_ =>
        {
            handlerIsBlocking.Set();
            releaseHandler.Wait();
        });
        var raiseReturned = false;

        var raiserThread = Task.Run(() =>
        {
            var raising = evt.RaiseAsync(1);
            Volatile.Write(ref raiseReturned, true);
            return raising;
        });
        var handlerStarted = handlerIsBlocking.Wait(Eventually.DefaultTimeout);
        clock.Advance(TimeSpan.FromMinutes(10));
        var returnedWhileHandlerBlocked = Volatile.Read(ref raiseReturned);
        releaseHandler.Set();
        //Task.Run unwraps the raise task, so this completes once the raise itself completed
        await raiserThread.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(handlerStarted).IsTrue();
        await Assert.That(returnedWhileHandlerBlocked).IsFalse();
        await Assert.That(Volatile.Read(ref raiseReturned)).IsTrue();
    }
}
