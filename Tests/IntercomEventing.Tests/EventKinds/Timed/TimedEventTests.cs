using System.Collections.Concurrent;
using IntercomEventing.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace IntercomEventing.Tests.EventKinds.Timed;

/// <summary>
/// <see cref="TimedEvent{TPayload}"/> on a <see cref="FakeTimeProvider"/>: each test advances the clock one interval at a
/// time and waits for the tick it caused, so tick counts are exact. <c>Start</c> starts or replaces the one schedule,
/// <c>Stop</c> never waits, and failures outside handlers are routed instead of lost.
/// </summary>
public class TimedEventTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    //ambient per-request state, like Activity.Current or a logging scope
    private static readonly AsyncLocal<string?> s_requestId = new();

    //long enough that a Start which waited for the handler could not look like one that returned at once
    private static readonly TimeSpan SlowSynchronousHandler = TimeSpan.FromMilliseconds(500);

    public static IEnumerable<TimeSpan> UnsupportedIntervals()
    {
        yield return TimeSpan.FromTicks(5_000);
        yield return TimeSpan.Zero;
        yield return TimeSpan.FromMilliseconds(-1);
        yield return TimeSpan.FromDays(50);
    }

    [Test]
    public async Task Start_RaisesOnEveryIntervalWithIncreasingTickNumbersAndTheStartPayload()
    {
        var clock = new FakeTimeProvider();
        var timer = new TimedEvent<string>(Interval, options: new EventingOptions { TimeProvider = clock });
        var ticks = new ConcurrentQueue<(string Payload, long TickNumber, TimeSpan Interval)>();
        timer.Subscribe(call => ticks.Enqueue((call.Payload, call.TickNumber, call.Interval)));

        timer.Start("heartbeat");
        for (var tick = 1; tick <= 3; tick++)
        {
            await AdvanceOneIntervalAndWaitAsync(clock, () => ticks.Count == tick);
        }

        await Assert.That(ticks).IsEquivalentTo([("heartbeat", 1L, Interval), ("heartbeat", 2L, Interval), ("heartbeat", 3L, Interval)], CollectionOrdering.Matching);
        await Assert.That(timer.IsRunning).IsTrue();
        await timer.DisposeAsync();
    }

    [Test]
    public async Task FirstTick_IsDueOneIntervalAfterStartAndNeverRaisedInsideStart()
    {
        var clock = new FakeTimeProvider();
        var timer = new TimedEvent(Interval, options: new EventingOptions { TimeProvider = clock });
        var ticks = 0;
        timer.Subscribe(_ =>
        {
            Interlocked.Increment(ref ticks);
        });

        timer.Start();
        var ticksRightAfterStart = Volatile.Read(ref ticks);
        var runningAfterStart = timer.IsRunning;
        clock.Advance(Interval - TimeSpan.FromMilliseconds(1));
        var ticksJustBeforeTheInterval = Volatile.Read(ref ticks);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var tickedAtTheInterval = await Eventually.IsTrueAsync(() => Volatile.Read(ref ticks) == 1);

        await Assert.That(ticksRightAfterStart).IsEqualTo(0);
        await Assert.That(runningAfterStart).IsTrue();
        await Assert.That(ticksJustBeforeTheInterval).IsEqualTo(0);
        await Assert.That(tickedAtTheInterval).IsTrue();
        await timer.DisposeAsync();
    }

    [Test]
    public async Task Start_WhenTheFirstTickIsAlreadyDueAtItsFirstWait_StillRaisesInTheBackground()
    {
        //core-port fix: a first wait that completed synchronously used to run the tick, handlers included, inside Start
        var clock = new PausingClock();
        var timer = new TimedEvent(TimeSpan.FromMilliseconds(5), autoReset: false, options: new EventingOptions { TimeProvider = clock });
        var handlerThread = -1;
        var handlerFinished = Gate.Create();
        timer.Subscribe(_ =>
        {
            handlerThread = Environment.CurrentManagedThreadId;
            Thread.Sleep(SlowSynchronousHandler);
            handlerFinished.TrySetResult();
        });
        var startThread = -1;
        var startReturnedBeforeTheHandlerFinished = false;
        var starter = new Thread(() =>
        {
            startThread = Environment.CurrentManagedThreadId;
            clock.PauseNextTimerCreation = true;
            timer.Start();
            startReturnedBeforeTheHandlerFinished = !handlerFinished.Task.IsCompleted;
        });

        starter.Start();
        var starterFinished = starter.Join(Eventually.DefaultTimeout);
        await handlerFinished.Task.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(starterFinished).IsTrue();
        await Assert.That(startReturnedBeforeTheHandlerFinished).IsTrue();
        await Assert.That(handlerThread).IsNotEqualTo(startThread);
        await timer.DisposeAsync();
    }

    [Test]
    public async Task StartWhileRunning_ReplacesTheScheduleAndItsPayload()
    {
        //S9: a second Start() in 1.2.0 killed one loop with an InvalidOperationException nobody observed
        var clock = new FakeTimeProvider();
        var timer = new TimedEvent<string>(Interval, options: new EventingOptions { TimeProvider = clock });
        var ticks = new ConcurrentQueue<(string Payload, long TickNumber)>();
        timer.Subscribe(call => ticks.Enqueue((call.Payload, call.TickNumber)));

        timer.Start("A");
        await AdvanceOneIntervalAndWaitAsync(clock, () => ticks.Count == 1);
        await AdvanceOneIntervalAndWaitAsync(clock, () => ticks.Count == 2);
        timer.Start("B");
        await AdvanceOneIntervalAndWaitAsync(clock, () => ticks.Count == 3);
        await AdvanceOneIntervalAndWaitAsync(clock, () => ticks.Count == 4);

        await Assert.That(ticks).IsEquivalentTo([("A", 1L), ("A", 2L), ("B", 1L), ("B", 2L)], CollectionOrdering.Matching);
        await timer.DisposeAsync();
    }

    [Test]
    public async Task Stop_EndsTheScheduleAtOnce()
    {
        var clock = new FakeTimeProvider();
        var timer = new TimedEvent(Interval, options: new EventingOptions { TimeProvider = clock });
        var ticks = 0;
        timer.Subscribe(_ =>
        {
            Interlocked.Increment(ref ticks);
        });
        timer.Start();
        await AdvanceOneIntervalAndWaitAsync(clock, () => Volatile.Read(ref ticks) == 1);

        timer.Stop();
        var runningAfterStop = timer.IsRunning;
        clock.Advance(Interval * 3);
        var ticksAfterStop = await WaitForAnyFurtherTicksAsync(() => Volatile.Read(ref ticks));
        timer.Stop();

        await Assert.That(runningAfterStop).IsFalse();
        await Assert.That(ticksAfterStop).IsEqualTo(1);
        await timer.DisposeAsync();
    }

    [Test]
    public async Task StartAfterStop_TicksAgainFromTickOne()
    {
        //S9: Start(), Stop(), Start() never ticked again in 1.2.0 because Stop disposed the only timer
        var clock = new FakeTimeProvider();
        var timer = new TimedEvent<int>(Interval, options: new EventingOptions { TimeProvider = clock });
        var ticks = new ConcurrentQueue<(int Payload, long TickNumber)>();
        timer.Subscribe(call => ticks.Enqueue((call.Payload, call.TickNumber)));
        timer.Start(1);
        await AdvanceOneIntervalAndWaitAsync(clock, () => ticks.Count == 1);
        timer.Stop();

        timer.Start(2);
        await AdvanceOneIntervalAndWaitAsync(clock, () => ticks.Count == 2);

        await Assert.That(ticks).IsEquivalentTo([(1, 1L), (2, 1L)], CollectionOrdering.Matching);
        await Assert.That(timer.IsRunning).IsTrue();
        await timer.DisposeAsync();
    }

    [Test]
    public async Task OneShot_RaisesOnceStopsAndCanBeStartedAgain()
    {
        //S9: a one-shot timer disposed its timer after the first tick in 1.2.0, so a second Start never ticked
        var clock = new FakeTimeProvider();
        var sessionExpired = new TimedEvent<SessionUser>(Interval, autoReset: false, options: new EventingOptions { TimeProvider = clock });
        var expiredSessions = new ConcurrentQueue<(string Name, long TickNumber)>();
        sessionExpired.Subscribe(call => expiredSessions.Enqueue((call.Payload.Name, call.TickNumber)));

        sessionExpired.Start(new SessionUser("Ada"));
        await AdvanceOneIntervalAndWaitAsync(clock, () => expiredSessions.Count == 1);
        var stoppedAfterItsTick = await Eventually.IsTrueAsync(() => !sessionExpired.IsRunning);
        clock.Advance(Interval * 3);
        var expiredWhileStopped = await WaitForAnyFurtherTicksAsync(() => expiredSessions.Count);
        sessionExpired.Start(new SessionUser("Grace"));
        await AdvanceOneIntervalAndWaitAsync(clock, () => expiredSessions.Count == 2);

        await Assert.That(stoppedAfterItsTick).IsTrue();
        await Assert.That(expiredWhileStopped).IsEqualTo(1);
        await Assert.That(expiredSessions).IsEquivalentTo([("Ada", 1L), ("Grace", 1L)], CollectionOrdering.Matching);
        await sessionExpired.DisposeAsync();
    }

    [Test]
    public async Task StopInsideAHandler_StillDeliversThatTickToTheOtherHandlers()
    {
        //S9b: the synthesis prototype's Stop() inside a handler cancelled delivery of that tick to the sibling handler
        var clock = new FakeTimeProvider();
        var pulse = new TimedEvent(Interval, options: new EventingOptions { TimeProvider = clock, DispatchMode = DispatchMode.Sequential });
        var stoppingHandlerCalls = 0;
        var siblingHandlerCalls = 0;
        var siblingTokenCancelled = true;
        pulse.Subscribe(_ =>
        {
            Interlocked.Increment(ref stoppingHandlerCalls);
            pulse.Stop();
        });
        pulse.Subscribe(call =>
        {
            siblingTokenCancelled = call.CancellationToken.IsCancellationRequested;
            Interlocked.Increment(ref siblingHandlerCalls);
        });

        pulse.Start();
        await AdvanceOneIntervalAndWaitAsync(clock, () => Volatile.Read(ref siblingHandlerCalls) == 1);
        clock.Advance(Interval * 3);
        var stoppingCallsAfterwards = await WaitForAnyFurtherTicksAsync(() => Volatile.Read(ref stoppingHandlerCalls));

        await Assert.That(stoppingCallsAfterwards).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref siblingHandlerCalls)).IsEqualTo(1);
        await Assert.That(siblingTokenCancelled).IsFalse();
        await Assert.That(pulse.IsRunning).IsFalse();
        await pulse.DisposeAsync();
    }

    [Test]
    public async Task StopAndStartInsideAHandler_RestartTheSchedule()
    {
        var clock = new FakeTimeProvider();
        var timer = new TimedEvent<int>(Interval, options: new EventingOptions { TimeProvider = clock });
        var ticks = new ConcurrentQueue<(int Payload, long TickNumber)>();
        timer.Subscribe(call =>
        {
            ticks.Enqueue((call.Payload, call.TickNumber));
            var isTheSecondTickOfTheFirstRun = call.Payload == 1 && call.TickNumber == 2;
            if (isTheSecondTickOfTheFirstRun)
            {
                timer.Stop();
                timer.Start(2);
            }
        });

        timer.Start(1);
        await AdvanceOneIntervalAndWaitAsync(clock, () => ticks.Count == 1);
        await AdvanceOneIntervalAndWaitAsync(clock, () => ticks.Count == 2);
        await AdvanceOneIntervalAndWaitAsync(clock, () => ticks.Count == 3);

        await Assert.That(ticks).IsEquivalentTo([(1, 1L), (1, 2L), (2, 1L)], CollectionOrdering.Matching);
        await Assert.That(timer.IsRunning).IsTrue();
        await timer.DisposeAsync();
    }

    [Test]
    public async Task TicksOfOneSchedule_NeverOverlap()
    {
        //documented: the next tick waits until the current tick's dispatch finished
        var clock = new FakeTimeProvider();
        var timer = new TimedEvent(Interval, options: new EventingOptions { TimeProvider = clock, StartNextEventHandlerAfter = null });
        var handlerGate = Gate.Create();
        var meter = new ConcurrencyMeter();
        timer.Subscribe(async _ =>
        {
            meter.Enter();
            await handlerGate.Task;
            meter.Exit();
        });

        timer.Start();
        await AdvanceOneIntervalAndWaitAsync(clock, () => meter.Entered == 1);
        clock.Advance(Interval);
        var ticksWhileTheFirstRuns = await WaitForAnyFurtherTicksAsync(() => meter.Entered);
        handlerGate.SetResult();
        var heldTickDelivered = await Eventually.IsTrueAsync(() => meter.Entered == 2);

        await Assert.That(ticksWhileTheFirstRuns).IsEqualTo(1);
        await Assert.That(heldTickDelivered).IsTrue();
        await Assert.That(meter.Peak).IsEqualTo(1);
        await timer.DisposeAsync();
    }

    [Test]
    public async Task StopThenStartBackToBackOnASingleThreadedContext_KeepsTicking()
    {
        //S9 (harness): restarting from a UI-style thread must neither block it nor leave the restarted schedule dead
        var ticksAfterRestart = 0;
        var completed = await SingleThreadedContext.RunAsync(async () =>
        {
            var timer = new TimedEvent(TimeSpan.FromMilliseconds(40), options: new EventingOptions());
            var restarted = false;
            timer.Subscribe(async _ =>
            {
                await Task.Delay(30);
                if (Volatile.Read(ref restarted))
                {
                    Interlocked.Increment(ref ticksAfterRestart);
                }
            });
            timer.Start();
            await Task.Delay(100);
            timer.Stop();
            timer.Start();
            Volatile.Write(ref restarted, true);
            await Eventually.IsTrueAsync(() => Volatile.Read(ref ticksAfterRestart) > 0);
            await timer.DisposeAsync();
        }, Eventually.DefaultTimeout);

        await Assert.That(completed).IsTrue();
        await Assert.That(Volatile.Read(ref ticksAfterRestart)).IsGreaterThan(0);
    }

    [Test]
    public async Task ScheduleFailure_IsRoutedAsBackgroundRaiseAndStopsTheSchedule()
    {
        //S9: a failing CreateEventCall on tick 2 surfaced only through UnobservedTaskException in 1.2.0
        var fakeClock = new FakeTimeProvider();
        var clock = new ThrowingClock(fakeClock);
        var failures = new FailureRecorder();
        var timer = new TimedEvent(Interval, options: new EventingOptions { TimeProvider = clock, DefaultExceptionHandler = failures.Record });
        var ticks = 0;
        timer.Subscribe(_ =>
        {
            Interlocked.Increment(ref ticks);
        });
        timer.Start();
        await AdvanceOneIntervalAndWaitAsync(fakeClock, () => Volatile.Read(ref ticks) == 1);

        clock.ThrowOnGetUtcNow = true;
        fakeClock.Advance(Interval);
        var routed = await Eventually.IsTrueAsync(() => failures.Count == 1);
        var stopped = await Eventually.IsTrueAsync(() => !timer.IsRunning);
        clock.ThrowOnGetUtcNow = false;
        timer.Start();
        var runningAgain = timer.IsRunning;
        await AdvanceOneIntervalAndWaitAsync(fakeClock, () => Volatile.Read(ref ticks) == 2);

        await Assert.That(routed).IsTrue();
        await Assert.That(stopped).IsTrue();
        var failure = failures.Failures.Single();
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.BackgroundRaise);
        await Assert.That(failure.Exception.Message).IsEqualTo("clock failure");
        await Assert.That(failure.SubscriptionId).IsNull();
        await Assert.That(failure.Call).IsNull();
        await Assert.That(runningAgain).IsTrue();
        await Assert.That(Volatile.Read(ref ticks)).IsEqualTo(2);
        await timer.DisposeAsync();
    }

    [Test]
    public async Task HandlerFailuresAndAThrowingDefaultHandler_DoNotStopTheSchedule()
    {
        //A8: a throwing DefaultExceptionHandler is contained, so the schedule keeps ticking
        var clock = new FakeTimeProvider();
        var timer = new TimedEvent(Interval, options: new EventingOptions
        {
            TimeProvider = clock,
            DefaultExceptionHandler = _ => throw new ApplicationException("default handler fault"),
        });
        var ticks = 0;
        timer.Subscribe(_ =>
        {
            Interlocked.Increment(ref ticks);
            throw new InvalidOperationException("tick handler failure");
        });

        timer.Start();
        for (var tick = 1; tick <= 3; tick++)
        {
            await AdvanceOneIntervalAndWaitAsync(clock, () => Volatile.Read(ref ticks) == tick);
        }

        await Assert.That(Volatile.Read(ref ticks)).IsEqualTo(3);
        await Assert.That(timer.IsRunning).IsTrue();
        await timer.DisposeAsync();
    }

    [Test]
    [MethodDataSource(nameof(UnsupportedIntervals))]
    public async Task Constructor_RejectsIntervalsOutsideTheSupportedRange(TimeSpan interval)
    {
        await Assert.That(() => new TimedEvent(interval, options: new EventingOptions())).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_AcceptsTheShortestAndLongestSupportedIntervals()
    {
        var shortest = new TimedEvent(TimeSpan.FromMilliseconds(1), options: new EventingOptions());
        var longest = new TimedEvent(TimeSpan.FromMilliseconds(uint.MaxValue - 1), options: new EventingOptions());

        await Assert.That(shortest.Interval).IsEqualTo(TimeSpan.FromMilliseconds(1));
        await Assert.That(longest.AutoReset).IsTrue();
        await Assert.That(longest.IsRunning).IsFalse();
    }

    [Test]
    public async Task Disposal_StopsTheScheduleAndLaterStartsThrow()
    {
        //A6
        var clock = new FakeTimeProvider();
        var timer = new TimedEvent(Interval, options: new EventingOptions { TimeProvider = clock });
        var ticks = 0;
        timer.Subscribe(_ =>
        {
            Interlocked.Increment(ref ticks);
        });
        timer.Start();

        await timer.DisposeAsync();
        clock.Advance(Interval * 3);
        var ticksAfterDisposal = await WaitForAnyFurtherTicksAsync(() => Volatile.Read(ref ticks));

        await Assert.That(timer.IsRunning).IsFalse();
        await Assert.That(ticksAfterDisposal).IsEqualTo(0);
        await Assert.That(() => timer.Start()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task DisposeAsyncInsideABackgroundedTickHandler_Completes()
    {
        //core-port fix: a tick handler moved to the background can await its own event's disposal
        var clock = new FakeTimeProvider();
        var longInterval = TimeSpan.FromHours(1);
        var handlerTimeout = TimeSpan.FromMilliseconds(10);
        var timer = new TimedEvent(longInterval, options: new EventingOptions { TimeProvider = clock, StartNextEventHandlerAfter = handlerTimeout });
        var handlerGate = Gate.Create();
        var disposedInsideTheHandler = Gate.Create();
        var handlerStarted = Gate.Create();
        timer.Subscribe(async _ =>
        {
            handlerStarted.TrySetResult();
            await handlerGate.Task;
            await timer.DisposeAsync();
            disposedInsideTheHandler.TrySetResult();
        });

        timer.Start();
        clock.Advance(longInterval);
        await handlerStarted.Task.WaitAsync(Eventually.DefaultTimeout);
        var movedToTheBackground = await FakeClockStepping.AdvancePastHandlerTimeoutUntilAsync(clock, handlerTimeout, () => timer.BackgroundWorkCount == 1);
        handlerGate.SetResult();
        var returned = await Eventually.CompletesAsync(disposedInsideTheHandler.Task);

        await Assert.That(movedToTheBackground).IsTrue();
        await Assert.That(returned).IsTrue();
        await Assert.That(timer.IsRunning).IsFalse();
    }

    [Test]
    public async Task ScheduleStartedInsideABackgroundedHandler_DoesNotBelongToThatHandler()
    {
        //Start detaches the new schedule from the handler calling it. Otherwise the restarted schedule's tick handlers would
        //count as part of the backgrounded handler that restarted the timer, and DisposeAsync awaited in one of them would
        //skip waiting for that still-running handler
        var clock = new FakeTimeProvider();
        var longInterval = TimeSpan.FromHours(1);
        var handlerTimeout = TimeSpan.FromMilliseconds(10);
        var timer = new TimedEvent<int>(longInterval, autoReset: false, options: new EventingOptions
        {
            TimeProvider = clock,
            StartNextEventHandlerAfter = handlerTimeout,
        });
        var restartingHandlerStarted = Gate.Create();
        var restartGate = Gate.Create();
        var restarted = Gate.Create();
        var finishRestartingHandlerGate = Gate.Create();
        var restartingHandlerFinished = false;
        var disposeReturned = Gate.Create<bool>();
        timer.Subscribe(async call =>
        {
            var isTheFirstSchedulesTick = call.Payload == 1;
            if (isTheFirstSchedulesTick)
            {
                restartingHandlerStarted.TrySetResult();
                await restartGate.Task;
                timer.Start(2);
                restarted.TrySetResult();
                await finishRestartingHandlerGate.Task;
                Volatile.Write(ref restartingHandlerFinished, true);
                return;
            }
            //a tick of the restarted schedule
            await timer.DisposeAsync();
            disposeReturned.TrySetResult(Volatile.Read(ref restartingHandlerFinished));
        });

        timer.Start(1);
        clock.Advance(longInterval);
        await restartingHandlerStarted.Task.WaitAsync(Eventually.DefaultTimeout);
        var movedToTheBackground = await FakeClockStepping.AdvancePastHandlerTimeoutUntilAsync(clock, handlerTimeout, () => timer.BackgroundWorkCount == 1);
        restartGate.SetResult();
        await restarted.Task.WaitAsync(Eventually.DefaultTimeout);
        clock.Advance(longInterval);
        var disposeWaitsForTheRestartingHandler = await Eventually.StaysIncompleteAsync(disposeReturned.Task);
        finishRestartingHandlerGate.SetResult();
        var restartingHandlerFinishedFirst = await disposeReturned.Task.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(movedToTheBackground).IsTrue();
        await Assert.That(disposeWaitsForTheRestartingHandler).IsTrue();
        await Assert.That(restartingHandlerFinishedFirst).IsTrue();
    }

    [Test]
    public async Task Ticks_DoNotRunInTheExecutionContextOfTheCodeThatCalledStart()
    {
        //a schedule started during a request must not hand that request's AsyncLocal values (Activity.Current, logging
        //scopes) to every tick for the life of the schedule
        var clock = new FakeTimeProvider();
        var timer = new TimedEvent(Interval, autoReset: true, new EventingOptions { TimeProvider = clock });
        var requestIdsSeenByTicks = new ConcurrentQueue<string>();
        timer.Subscribe(call => requestIdsSeenByTicks.Enqueue(s_requestId.Value ?? "(none)"));

        var requestIdSeenAfterStart = await StartInsideARequestAsync(timer, "request-42");
        for (var tick = 1; tick <= 3; tick++)
        {
            await AdvanceOneIntervalAndWaitAsync(clock, () => requestIdsSeenByTicks.Count == tick);
        }
        await timer.DisposeAsync();

        await Assert.That(requestIdSeenAfterStart).IsEqualTo("request-42");
        await Assert.That(requestIdsSeenByTicks).IsEquivalentTo(["(none)", "(none)", "(none)"]);
    }

    [Test]
    public async Task Start_InsideSuppressedExecutionContextFlow_StillStartsTheSchedule()
    {
        //Start suppresses the flow itself, which throws when the caller already suppressed it
        var clock = new FakeTimeProvider();
        var timer = new TimedEvent(Interval, autoReset: false, new EventingOptions { TimeProvider = clock });
        var tickNumbers = new ConcurrentQueue<long>();
        timer.Subscribe(call => tickNumbers.Enqueue(call.TickNumber));

        using (ExecutionContext.SuppressFlow())
        {
            timer.Start();
        }
        await AdvanceOneIntervalAndWaitAsync(clock, () => tickNumbers.Count == 1);
        await timer.DisposeAsync();

        await Assert.That(tickNumbers).IsEquivalentTo([1L]);
    }

    [Test]
    public async Task ConcurrentStartAndStopStorm_LeavesOnlyTheLastSchedule()
    {
        //no clock movement during the storm, so any tick afterwards comes from a schedule that is still alive
        var clock = new FakeTimeProvider();
        var timer = new TimedEvent<int>(Interval, options: new EventingOptions { TimeProvider = clock });
        var ticks = new ConcurrentQueue<(int Payload, long TickNumber)>();
        timer.Subscribe(call => ticks.Enqueue((call.Payload, call.TickNumber)));
        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var iteration = 0; iteration < 500; iteration++)
            {
                if (iteration % 3 == 0)
                {
                    timer.Stop();
                }
                else
                {
                    timer.Start(worker);
                }
            }
        })).ToArray();
        await Task.WhenAll(workers).WaitAsync(Eventually.DefaultTimeout);

        timer.Start(999);
        for (var tick = 1; tick <= 3; tick++)
        {
            await AdvanceOneIntervalAndWaitAsync(clock, () => ticks.Count >= tick);
        }
        var ticksOfTheLastSchedule = await WaitForAnyFurtherTicksAsync(() => ticks.Count);
        timer.Stop();
        clock.Advance(Interval * 3);
        var ticksAfterStop = await WaitForAnyFurtherTicksAsync(() => ticks.Count);

        await Assert.That(ticksOfTheLastSchedule).IsEqualTo(3);
        await Assert.That(ticks).IsEquivalentTo([(999, 1L), (999, 2L), (999, 3L)], CollectionOrdering.Matching);
        await Assert.That(ticksAfterStop).IsEqualTo(3);
        await Assert.That(timer.IsRunning).IsFalse();
        await timer.DisposeAsync();
    }

    //stands in for request handling that starts a schedule and returns: the AsyncLocal value lives only in this flow
    private static async Task<string?> StartInsideARequestAsync(TimedEvent timer, string requestId)
    {
        s_requestId.Value = requestId;
        timer.Start();
        await Task.Yield();
        return s_requestId.Value;
    }

    private static async Task AdvanceOneIntervalAndWaitAsync(FakeTimeProvider clock, Func<bool> tickArrived)
    {
        clock.Advance(Interval);
        var arrived = await Eventually.IsTrueAsync(tickArrived);
        if (!arrived)
        {
            throw new TimeoutException("The tick caused by advancing the fake clock never arrived.");
        }
    }

    //a tick that should not happen cannot be awaited, so give a wrongly running schedule a moment to deliver it
    private static async Task<int> WaitForAnyFurtherTicksAsync(Func<int> currentTicks)
    {
        await Task.Delay(Eventually.StillWaitingWindow);
        return currentTicks();
    }
}
