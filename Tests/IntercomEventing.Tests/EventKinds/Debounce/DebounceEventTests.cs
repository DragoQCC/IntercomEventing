using System.Collections.Concurrent;
using IntercomEventing.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace IntercomEventing.Tests.EventKinds.Debounce;

/// <summary>
/// <see cref="DebounceEvent{TPayload}"/> on a <see cref="FakeTimeProvider"/>: one raise per burst with the last trigger's
/// payload, <c>FlushAsync</c> to raise a pending trigger now and await it, and failures outside handlers routed.
/// </summary>
public class DebounceEventTests
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1);

    //long enough that a Trigger which waited for the handler could not look like one that returned at once
    private static readonly TimeSpan SlowSynchronousHandler = TimeSpan.FromMilliseconds(500);

    public static IEnumerable<TimeSpan> UnsupportedIntervals()
    {
        yield return TimeSpan.Zero;
        yield return TimeSpan.FromMilliseconds(-1);
        yield return TimeSpan.FromDays(50);
    }

    [Test]
    public async Task Burst_RaisesOnceWithTheLastPayloadCallerAndTriggerCount()
    {
        //sample section 7: keystrokes coalesce into one search
        var clock = new FakeTimeProvider();
        var searchChanged = new DebounceEvent<SearchQuery>(QuietPeriod, new EventingOptions { TimeProvider = clock });
        var calls = new ConcurrentQueue<DebounceEventCall<SearchQuery>>();
        searchChanged.Subscribe(call => calls.Enqueue(call));
        var lastKeystrokeSource = new object();

        foreach (var text in new[] { "I", "In", "Int", "Inte" })
        {
            searchChanged.Trigger(new SearchQuery(text), caller: new object());
        }
        searchChanged.Trigger(new SearchQuery("Inter"), caller: lastKeystrokeSource);
        var raisedInsideTrigger = !calls.IsEmpty;
        clock.Advance(QuietPeriod);
        var raised = await Eventually.IsTrueAsync(() => calls.Count == 1);

        await Assert.That(raisedInsideTrigger).IsFalse();
        await Assert.That(raised).IsTrue();
        var call = calls.Single();
        await Assert.That(call.Payload.Text).IsEqualTo("Inter");
        await Assert.That(call.Metadata.Caller).IsSameReferenceAs(lastKeystrokeSource);
        await Assert.That(call.CoalescedTriggerCount).IsEqualTo(5);
        await Assert.That(call.DebounceInterval).IsEqualTo(QuietPeriod);
        await searchChanged.DisposeAsync();
    }

    [Test]
    public async Task Trigger_WhenTheQuietPeriodIsAlreadyOverAtItsFirstWait_StillRaisesInTheBackground()
    {
        //core-port fix: a quiet-period wait that completed synchronously used to raise, handlers included, inside Trigger
        var clock = new PausingClock();
        var debounce = new DebounceEvent(TimeSpan.FromMilliseconds(5), new EventingOptions { TimeProvider = clock });
        var handlerThread = -1;
        var handlerFinished = Gate.Create();
        debounce.Subscribe(_ =>
        {
            handlerThread = Environment.CurrentManagedThreadId;
            Thread.Sleep(SlowSynchronousHandler);
            handlerFinished.TrySetResult();
        });
        var triggerThread = -1;
        var triggerReturnedBeforeTheHandlerFinished = false;
        var triggerer = new Thread(() =>
        {
            triggerThread = Environment.CurrentManagedThreadId;
            clock.PauseNextTimerCreation = true;
            debounce.Trigger();
            triggerReturnedBeforeTheHandlerFinished = !handlerFinished.Task.IsCompleted;
        });

        triggerer.Start();
        var triggererFinished = triggerer.Join(Eventually.DefaultTimeout);
        await handlerFinished.Task.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(triggererFinished).IsTrue();
        await Assert.That(triggerReturnedBeforeTheHandlerFinished).IsTrue();
        await Assert.That(handlerThread).IsNotEqualTo(triggerThread);
        await debounce.DisposeAsync();
    }

    [Test]
    public async Task EachTrigger_RestartsTheQuietPeriod()
    {
        var clock = new FakeTimeProvider();
        var debounce = new DebounceEvent(QuietPeriod, new EventingOptions { TimeProvider = clock });
        var raises = 0;
        debounce.Subscribe(_ =>
        {
            Interlocked.Increment(ref raises);
        });
        var almostTheQuietPeriod = QuietPeriod - TimeSpan.FromMilliseconds(100);

        debounce.Trigger();
        clock.Advance(almostTheQuietPeriod);
        debounce.Trigger();
        clock.Advance(almostTheQuietPeriod);
        var raisesBeforeAFullQuietPeriod = Volatile.Read(ref raises);
        var pendingBeforeAFullQuietPeriod = debounce.HasPendingRaise;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var raised = await Eventually.IsTrueAsync(() => Volatile.Read(ref raises) == 1);

        await Assert.That(raisesBeforeAFullQuietPeriod).IsEqualTo(0);
        await Assert.That(pendingBeforeAFullQuietPeriod).IsTrue();
        await Assert.That(raised).IsTrue();
        await Assert.That(debounce.HasPendingRaise).IsFalse();
        await debounce.DisposeAsync();
    }

    [Test]
    public async Task TwoBursts_ProduceTwoRaisesWithTheLastPayloadOfEach()
    {
        var clock = new FakeTimeProvider();
        var searchChanged = new DebounceEvent<SearchQuery>(QuietPeriod, new EventingOptions { TimeProvider = clock });
        var raises = new ConcurrentQueue<string>();
        searchChanged.Subscribe(call => raises.Enqueue($"{call.Payload.Text}x{call.CoalescedTriggerCount}"));

        foreach (var text in new[] { "I", "In", "Int", "Inte", "Inter", "Interc", "Interco", "Intercom" })
        {
            searchChanged.Trigger(new SearchQuery(text));
            clock.Advance(TimeSpan.FromMilliseconds(40));
        }
        clock.Advance(QuietPeriod);
        await Eventually.IsTrueAsync(() => raises.Count == 1);
        foreach (var text in new[] { "Ev", "Eve" })
        {
            searchChanged.Trigger(new SearchQuery(text));
            clock.Advance(TimeSpan.FromMilliseconds(40));
        }
        clock.Advance(QuietPeriod);
        await Eventually.IsTrueAsync(() => raises.Count == 2);

        await Assert.That(raises).IsEquivalentTo(["Intercomx8", "Evex2"], CollectionOrdering.Matching);
        await searchChanged.DisposeAsync();
    }

    [Test]
    public async Task ConcurrentTriggers_CoalesceIntoOneRaise()
    {
        //harness DB1: 2000 concurrent triggers give one raise that counts all of them
        var clock = new FakeTimeProvider();
        var debounce = new DebounceEvent<int>(QuietPeriod, new EventingOptions { TimeProvider = clock });
        var raises = new ConcurrentQueue<int>();
        debounce.Subscribe(call => raises.Enqueue(call.CoalescedTriggerCount));

        Parallel.For(0, 2000, index => debounce.Trigger(index));
        clock.Advance(QuietPeriod);
        var raised = await Eventually.IsTrueAsync(() => raises.Count == 1);
        clock.Advance(QuietPeriod * 2);
        var raisesAfterwards = await WaitForAnyFurtherRaisesAsync(() => raises.Count);

        await Assert.That(raised).IsTrue();
        await Assert.That(raisesAfterwards).IsEqualTo(1);
        await Assert.That(raises).IsEquivalentTo([2000]);
        await debounce.DisposeAsync();
    }

    [Test]
    public async Task TriggerDuringARaiseThatIsStillDelivering_StartsANewBurstWithoutWaiting()
    {
        //S13: in 1.2.0 the handler ran inside the debounce lock, so a second trigger waited 804 ms behind a slow handler
        var clock = new FakeTimeProvider();
        var debounce = new DebounceEvent<int>(QuietPeriod, new EventingOptions { TimeProvider = clock, StartNextEventHandlerAfter = null });
        var firstHandlerGate = Gate.Create();
        var firstHandlerEntered = Gate.Create();
        var delivered = new ConcurrentQueue<int>();
        debounce.Subscribe(async call =>
        {
            delivered.Enqueue(call.Payload);
            if (call.Payload == 1)
            {
                firstHandlerEntered.TrySetResult();
                await firstHandlerGate.Task;
            }
        });

        debounce.Trigger(1);
        clock.Advance(QuietPeriod);
        await firstHandlerEntered.Task.WaitAsync(Eventually.DefaultTimeout);
        var triggerFromAnotherThread = Task.Run(() =>
        {
            debounce.Trigger(2);
            return debounce.HasPendingRaise;
        });
        var triggerReturned = await Eventually.CompletesAsync(triggerFromAnotherThread);
        var pendingAfterTheTrigger = await triggerFromAnotherThread;
        clock.Advance(QuietPeriod);
        var secondBurstDelivered = await Eventually.IsTrueAsync(() => delivered.Contains(2));
        var firstHandlerStillWaiting = !firstHandlerGate.Task.IsCompleted;
        firstHandlerGate.SetResult();

        await Assert.That(triggerReturned).IsTrue();
        await Assert.That(pendingAfterTheTrigger).IsTrue();
        await Assert.That(secondBurstDelivered).IsTrue();
        await Assert.That(firstHandlerStillWaiting).IsTrue();
        await debounce.DisposeAsync().AsTask().WaitAsync(Eventually.DefaultTimeout);
    }

    [Test]
    public async Task AsyncHandlerFault_IsRouted()
    {
        //S13c: an async handler fault was routed 0 times in 1.2.0
        var clock = new FakeTimeProvider();
        var failures = new FailureRecorder();
        var debounce = new DebounceEvent(QuietPeriod, new EventingOptions { TimeProvider = clock, DefaultExceptionHandler = failures.Record });
        debounce.Subscribe(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("search failed");
        });

        debounce.Trigger();
        clock.Advance(QuietPeriod);
        var routed = await Eventually.IsTrueAsync(() => failures.Count == 1);

        await Assert.That(routed).IsTrue();
        await Assert.That(failures.Failures[0].Stage).IsEqualTo(EventFailureStage.Handler);
        await debounce.DisposeAsync();
    }

    [Test]
    public async Task RaiseFailureOutsideHandlers_IsRoutedAsBackgroundRaiseAndNothingStaysPending()
    {
        //S13: a failing CreateEventCall reached nobody in 1.2.0 ("No input available" only via UnobservedTaskException)
        var fakeClock = new FakeTimeProvider();
        var clock = new ThrowingClock(fakeClock);
        var failures = new FailureRecorder();
        var debounce = new DebounceEvent<int>(QuietPeriod, new EventingOptions { TimeProvider = clock, DefaultExceptionHandler = failures.Record });
        var raises = new ConcurrentQueue<int>();
        debounce.Subscribe(call => raises.Enqueue(call.Payload));

        clock.ThrowOnGetUtcNow = true;
        debounce.Trigger(1);
        fakeClock.Advance(QuietPeriod);
        var routed = await Eventually.IsTrueAsync(() => failures.Count == 1);
        var pendingAfterTheFailure = debounce.HasPendingRaise;
        clock.ThrowOnGetUtcNow = false;
        debounce.Trigger(2);
        fakeClock.Advance(QuietPeriod);
        var laterTriggerRaised = await Eventually.IsTrueAsync(() => raises.Count == 1);

        await Assert.That(routed).IsTrue();
        var failure = failures.Failures.Single();
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.BackgroundRaise);
        await Assert.That(failure.Exception.Message).IsEqualTo("clock failure");
        await Assert.That(pendingAfterTheFailure).IsFalse();
        await Assert.That(laterTriggerRaised).IsTrue();
        await Assert.That(raises).IsEquivalentTo([2]);
        await debounce.DisposeAsync();
    }

    [Test]
    public async Task FlushAsync_WithNothingPending_ReturnsFalseAndRaisesNothing()
    {
        var debounce = new DebounceEvent<string>(QuietPeriod, new EventingOptions { TimeProvider = new FakeTimeProvider() });
        var raises = 0;
        debounce.Subscribe(_ =>
        {
            Interlocked.Increment(ref raises);
        });

        var flushed = await debounce.FlushAsync();

        await Assert.That(flushed).IsFalse();
        await Assert.That(Volatile.Read(ref raises)).IsEqualTo(0);
    }

    [Test]
    public async Task FlushAsync_RaisesThePendingTriggerNowAndNotAgainAfterTheQuietPeriod()
    {
        var clock = new FakeTimeProvider();
        var debounce = new DebounceEvent<string>(QuietPeriod, new EventingOptions { TimeProvider = clock });
        var calls = new ConcurrentQueue<(string Payload, int Coalesced)>();
        debounce.Subscribe(call => calls.Enqueue((call.Payload, call.CoalescedTriggerCount)));
        debounce.Trigger("a");
        debounce.Trigger("ab");
        debounce.Trigger("abc");

        var flushed = await debounce.FlushAsync();
        var callsRightAfterTheFlush = calls.ToArray();
        var pendingAfterTheFlush = debounce.HasPendingRaise;
        clock.Advance(QuietPeriod * 2);
        var callsAfterTheQuietPeriod = await WaitForAnyFurtherRaisesAsync(() => calls.Count);
        debounce.Trigger("d");
        clock.Advance(QuietPeriod);
        var normalDebounceStillWorks = await Eventually.IsTrueAsync(() => calls.Count == 2);

        await Assert.That(flushed).IsTrue();
        await Assert.That(callsRightAfterTheFlush).IsEquivalentTo([("abc", 3)], CollectionOrdering.Matching);
        await Assert.That(pendingAfterTheFlush).IsFalse();
        await Assert.That(callsAfterTheQuietPeriod).IsEqualTo(1);
        await Assert.That(normalDebounceStillWorks).IsTrue();
        await Assert.That(calls.Last()).IsEqualTo(("d", 1));
        await debounce.DisposeAsync();
    }

    [Test]
    public async Task FlushAsync_CompletesOnlyAfterTheHandlersFinished()
    {
        //S13 / A12: in 1.2.0 nothing could await the debounced raise ('await NotifyTyped' returned before the handler ran)
        var debounce = new DebounceEvent<int>(QuietPeriod, new EventingOptions { TimeProvider = new FakeTimeProvider(), StartNextEventHandlerAfter = null });
        var handlerGate = Gate.Create();
        var handlerFinished = false;
        debounce.Subscribe(async _ =>
        {
            await handlerGate.Task;
            Volatile.Write(ref handlerFinished, true);
        });
        debounce.Trigger(1);

        var flushing = debounce.FlushAsync();
        var completedBeforeTheHandler = flushing.IsCompleted;
        handlerGate.SetResult();
        var flushed = await flushing.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(completedBeforeTheHandler).IsFalse();
        await Assert.That(flushed).IsTrue();
        await Assert.That(Volatile.Read(ref handlerFinished)).IsTrue();
        await debounce.DisposeAsync();
    }

    [Test]
    public async Task FlushAsync_WithACancelledToken_KeepsTheTriggerPending()
    {
        var clock = new FakeTimeProvider();
        var debounce = new DebounceEvent<string>(QuietPeriod, new EventingOptions { TimeProvider = clock });
        var calls = new ConcurrentQueue<(string Payload, int Coalesced)>();
        debounce.Subscribe(call => calls.Enqueue((call.Payload, call.CoalescedTriggerCount)));
        debounce.Trigger("x");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.That(async () => await debounce.FlushAsync(cancelled.Token)).Throws<OperationCanceledException>();
        var stillPending = debounce.HasPendingRaise;
        clock.Advance(QuietPeriod);
        var raisedAfterItsQuietPeriod = await Eventually.IsTrueAsync(() => calls.Count == 1);

        await Assert.That(stillPending).IsTrue();
        await Assert.That(raisedAfterItsQuietPeriod).IsTrue();
        await Assert.That(calls.Single()).IsEqualTo(("x", 1));
        await debounce.DisposeAsync();
    }

    [Test]
    public async Task FlushAsync_RacingTheEndOfTheQuietPeriod_RaisesExactlyOnce()
    {
        var roundsWithoutExactlyOneRaise = 0;
        for (var round = 0; round < 100; round++)
        {
            var clock = new FakeTimeProvider();
            var debounce = new DebounceEvent<int>(QuietPeriod, new EventingOptions { TimeProvider = clock });
            var raises = 0;
            debounce.Subscribe(_ =>
            {
                Interlocked.Increment(ref raises);
            });
            debounce.Trigger(round);

            var quietPeriodEnding = Task.Run(() => clock.Advance(QuietPeriod));
            var flushing = Task.Run(() => debounce.FlushAsync());
            await Task.WhenAll(quietPeriodEnding, flushing).WaitAsync(Eventually.DefaultTimeout);
            await Eventually.IsTrueAsync(() => Volatile.Read(ref raises) >= 1);
            await Task.Delay(2);
            if (Volatile.Read(ref raises) != 1)
            {
                roundsWithoutExactlyOneRaise++;
            }
            await debounce.DisposeAsync();
        }

        await Assert.That(roundsWithoutExactlyOneRaise).IsEqualTo(0);
    }

    [Test]
    public async Task FlushAsync_AfterTheBackgroundRaiseStarted_ReturnsFalseWithoutWaitingForIt()
    {
        //documented: once the quiet period ended the background raise owns the trigger
        var clock = new FakeTimeProvider();
        var debounce = new DebounceEvent<int>(QuietPeriod, new EventingOptions { TimeProvider = clock, StartNextEventHandlerAfter = null });
        var handlerGate = Gate.Create();
        var handlerEntered = Gate.Create();
        debounce.Subscribe(async _ =>
        {
            handlerEntered.TrySetResult();
            await handlerGate.Task;
        });
        debounce.Trigger(1);
        clock.Advance(QuietPeriod);
        await handlerEntered.Task.WaitAsync(Eventually.DefaultTimeout);

        var flushing = debounce.FlushAsync();
        var returnedAtOnce = flushing.IsCompleted;
        var flushed = await flushing;
        handlerGate.SetResult();

        await Assert.That(returnedAtOnce).IsTrue();
        await Assert.That(flushed).IsFalse();
        await debounce.DisposeAsync().AsTask().WaitAsync(Eventually.DefaultTimeout);
    }

    [Test]
    public async Task Disposal_DropsAPendingTrigger()
    {
        var clock = new FakeTimeProvider();
        var debounce = new DebounceEvent<int>(QuietPeriod, new EventingOptions { TimeProvider = clock });
        var raises = 0;
        debounce.Subscribe(_ =>
        {
            Interlocked.Increment(ref raises);
        });
        debounce.Trigger(1);

        await debounce.DisposeAsync();
        clock.Advance(QuietPeriod * 2);
        var raisesAfterDisposal = await WaitForAnyFurtherRaisesAsync(() => Volatile.Read(ref raises));

        await Assert.That(raisesAfterDisposal).IsEqualTo(0);
        await Assert.That(debounce.HasPendingRaise).IsFalse();
    }

    [Test]
    public async Task TriggerAndFlushAfterDisposal_ThrowObjectDisposedException()
    {
        //A6
        var debounce = new DebounceEvent(QuietPeriod, new EventingOptions { TimeProvider = new FakeTimeProvider() });
        await debounce.DisposeAsync();

        await Assert.That(() => debounce.Trigger()).Throws<ObjectDisposedException>();
        await Assert.That(async () => await debounce.FlushAsync()).Throws<ObjectDisposedException>();
    }

    [Test]
    [MethodDataSource(nameof(UnsupportedIntervals))]
    public async Task Constructor_RejectsIntervalsOutsideTheSupportedRange(TimeSpan debounceInterval)
    {
        await Assert.That(() => new DebounceEvent(debounceInterval, new EventingOptions())).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DisposeAsyncInsideAHandler_Completes()
    {
        var clock = new FakeTimeProvider();
        var debounce = new DebounceEvent<int>(QuietPeriod, new EventingOptions { TimeProvider = clock });
        var disposedInsideTheHandler = Gate.Create();
        debounce.Subscribe(async _ =>
        {
            await Task.Yield();
            await debounce.DisposeAsync();
            disposedInsideTheHandler.TrySetResult();
        });

        debounce.Trigger(1);
        clock.Advance(QuietPeriod);
        var returned = await Eventually.CompletesAsync(disposedInsideTheHandler.Task);

        await Assert.That(returned).IsTrue();
        await Assert.That(debounce.IsDisposed).IsTrue();
    }

    [Test]
    public async Task TriggerInsideABackgroundedHandler_DoesNotMakeTheNextRaisePartOfThatHandler()
    {
        //Trigger detaches its background wait from the handler calling it. Otherwise the next raise's handlers would count as
        //part of the backgrounded handler that triggered it, and DisposeAsync awaited in one of them would skip waiting for
        //that still-running handler
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(10);
        var debounce = new DebounceEvent<int>(QuietPeriod, new EventingOptions { TimeProvider = clock, StartNextEventHandlerAfter = handlerTimeout });
        var triggeringHandlerStarted = Gate.Create();
        var triggerGate = Gate.Create();
        var triggered = Gate.Create();
        var finishTriggeringHandlerGate = Gate.Create();
        var triggeringHandlerFinished = false;
        var disposeReturned = Gate.Create<bool>();
        debounce.Subscribe(async call =>
        {
            var isTheFirstRaise = call.Payload == 1;
            if (isTheFirstRaise)
            {
                triggeringHandlerStarted.TrySetResult();
                await triggerGate.Task;
                debounce.Trigger(2);
                triggered.TrySetResult();
                await finishTriggeringHandlerGate.Task;
                Volatile.Write(ref triggeringHandlerFinished, true);
                return;
            }
            //the raise of the trigger made inside the first handler
            await debounce.DisposeAsync();
            disposeReturned.TrySetResult(Volatile.Read(ref triggeringHandlerFinished));
        });

        debounce.Trigger(1);
        clock.Advance(QuietPeriod);
        await triggeringHandlerStarted.Task.WaitAsync(Eventually.DefaultTimeout);
        var movedToTheBackground = await FakeClockStepping.AdvancePastHandlerTimeoutUntilAsync(clock, handlerTimeout, () => debounce.BackgroundWorkCount == 1);
        triggerGate.SetResult();
        await triggered.Task.WaitAsync(Eventually.DefaultTimeout);
        clock.Advance(QuietPeriod);
        var disposeWaitsForTheTriggeringHandler = await Eventually.StaysIncompleteAsync(disposeReturned.Task);
        finishTriggeringHandlerGate.SetResult();
        var triggeringHandlerFinishedFirst = await disposeReturned.Task.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(movedToTheBackground).IsTrue();
        await Assert.That(disposeWaitsForTheTriggeringHandler).IsTrue();
        await Assert.That(triggeringHandlerFinishedFirst).IsTrue();
    }

    //a raise that should not happen cannot be awaited, so give a wrongly pending trigger a moment to raise
    private static async Task<int> WaitForAnyFurtherRaisesAsync(Func<int> currentRaises)
    {
        await Task.Delay(Eventually.StillWaitingWindow);
        return currentRaises();
    }
}
