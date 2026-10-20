using IntercomEventing.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// Where handler exceptions go: the subscription's own exception handler first, else
/// <see cref="EventingOptions.DefaultExceptionHandler"/>, else nowhere. They never reach the raiser, never stop the other
/// handlers, and a throwing exception handler is contained as well.
/// </summary>
public class ExceptionRoutingTests
{
    //the dispatcher waits StartNextEventHandlerAfter plus this documented grace before moving a handler to the background
    private static readonly TimeSpan DocumentedTimeoutGrace = TimeSpan.FromMilliseconds(50);

    [Test]
    public async Task AsyncFaultsThatFinishBeforeTheTimeout_AreRouted()
    {
        //S1: 1.2.0 ignored a handler task that faulted before StartNextEventHandlerAfter, so these exceptions reached nobody.
        //The default timeout stays in force, but on a fake clock that never moves: the raise always waits for these handlers,
        //even when a loaded machine delays their continuations past the real-time timeout
        var defaultFailures = new FailureRecorder();
        var subscriptionFailures = new List<Exception>();
        var evt = new IntercomEvent<int>(new EventingOptions { DefaultExceptionHandler = defaultFailures.Record, TimeProvider = new FakeTimeProvider() });
        evt.Subscribe(async _ =>
        {
            await Task.Delay(10);
            throw new InvalidOperationException("after await");
        }, (exception, _) =>
        {
            lock (subscriptionFailures)
            {
                subscriptionFailures.Add(exception);
            }
        });
        evt.Subscribe(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("after yield");
        });
        Func<EventCall<int>, Task> throwsBeforeReturningItsTask = _ => throw new InvalidOperationException("synchronous");
        evt.Subscribe(throwsBeforeReturningItsTask);

        await evt.RaiseAsync(1);

        await Assert.That(subscriptionFailures).Count().IsEqualTo(1);
        await Assert.That(subscriptionFailures[0].Message).IsEqualTo("after await");
        var defaultMessages = defaultFailures.Failures.Select(failure => failure.Exception.Message).ToArray();
        await Assert.That(defaultMessages).IsEquivalentTo(["after yield", "synchronous"]);
    }

    [Test]
    [Arguments(DispatchMode.Parallel)]
    [Arguments(DispatchMode.Sequential)]
    public async Task EveryFaultingSubscriber_IsRoutedInEitherDispatchMode(DispatchMode dispatchMode)
    {
        //S1 cases 6 and 7: three faulting subscribers in parallel and in sequential mode (handler timeout on a clock that never moves)
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            DispatchMode = dispatchMode,
            DefaultExceptionHandler = failures.Record,
            TimeProvider = new FakeTimeProvider(),
        });
        for (var handlerNumber = 0; handlerNumber < 3; handlerNumber++)
        {
            evt.Subscribe(async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("handler failure");
            });
        }

        await evt.RaiseAsync(1);

        await Assert.That(failures.Count).IsEqualTo(3);
    }

    [Test]
    public async Task SubscriptionExceptionHandler_TakesPrecedenceOverTheDefault()
    {
        var defaultFailures = new FailureRecorder();
        var subscriptionFailures = 0;
        var evt = new IntercomEvent<int>(new EventingOptions { DefaultExceptionHandler = defaultFailures.Record });
        await evt.SubscribeAsync(ThrowAsync, new()
        {
            OnException = (_, _) => Interlocked.Increment(ref subscriptionFailures),
        });

        await evt.RaiseAsync(1);

        await Assert.That(Volatile.Read(ref subscriptionFailures)).IsEqualTo(1);
        await Assert.That(defaultFailures.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WithoutAnyExceptionHandler_TheFailureIsSwallowedAndTheOtherHandlersStillRun()
    {
        var evt = new IntercomEvent<int>(new EventingOptions { DispatchMode = DispatchMode.Sequential });
        var healthyHandlerRan = false;
        evt.Subscribe(ThrowAsync);
        evt.Subscribe(_ =>
        {
            healthyHandlerRan = true;
        });

        var raising = evt.RaiseAsync(1);
        await raising;

        await Assert.That(raising.IsCompletedSuccessfully).IsTrue();
        await Assert.That(healthyHandlerRan).IsTrue();
    }

    [Test]
    public async Task FailureRecord_DescribesTheFailingSubscriptionAndCall()
    {
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions { DefaultExceptionHandler = failures.Record }) { Name = "AuditTrail" };
        var thrownException = new InvalidOperationException("audit store unavailable");
        EventCall<int>? deliveredCall = null;
        var subscription = evt.Subscribe(call =>
        {
            deliveredCall = call;
            throw thrownException;
        });

        await evt.RaiseAsync(42);

        var failure = failures.Failures.Single();
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.Handler);
        await Assert.That(failure.EventName).IsEqualTo("AuditTrail");
        await Assert.That(failure.SubscriptionId).IsEqualTo(subscription.Id);
        await Assert.That(failure.Exception).IsSameReferenceAs(thrownException);
        await Assert.That(failure.Call).IsSameReferenceAs(deliveredCall);
    }

    [Test]
    public async Task ThrowingExceptionHandlers_AreContainedAndTheRaiseCompletes()
    {
        //A8: in 1.2.0 an exception thrown by an exception handler escaped to the raiser
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            DispatchMode = DispatchMode.Sequential,
            DefaultExceptionHandler = _ => throw new ApplicationException("default handler fault"),
        });
        var laterHandlerRan = false;
        evt.Subscribe(ThrowAsync, (_, _) => throw new ApplicationException("subscription handler fault"));
        evt.Subscribe(ThrowAsync);
        evt.Subscribe(_ =>
        {
            laterHandlerRan = true;
        });

        var raising = evt.RaiseAsync(1);
        await raising;

        await Assert.That(raising.IsCompletedSuccessfully).IsTrue();
        await Assert.That(laterHandlerRan).IsTrue();
    }

    [Test]
    public async Task ThrowingDefaultHandler_ForABackgroundedHandler_IsContained()
    {
        //A8: the same throw in the background path faulted an untracked Task.Run in 1.2.0; now disposal still drains cleanly
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(100);
        var defaultHandlerCalls = 0;
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            StartNextEventHandlerAfter = handlerTimeout,
            TimeProvider = clock,
            DefaultExceptionHandler = _ =>
            {
                Interlocked.Increment(ref defaultHandlerCalls);
                throw new ApplicationException("default handler fault");
            },
        });
        var handlerGate = Gate.Create();
        evt.Subscribe(async _ =>
        {
            await handlerGate.Task;
            throw new InvalidOperationException("slow handler failure");
        });

        var raising = evt.RaiseAsync(1);
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);
        handlerGate.SetResult();
        var routed = await Eventually.IsTrueAsync(() => Volatile.Read(ref defaultHandlerCalls) == 1);
        var drained = await Eventually.IsTrueAsync(() => evt.BackgroundWorkCount == 0);
        await evt.DisposeAsync().AsTask().WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(routed).IsTrue();
        await Assert.That(drained).IsTrue();
    }

    [Test]
    public async Task SlowHandlerFailure_IsStillRoutedAfterItMovedToTheBackground()
    {
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(100);
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            StartNextEventHandlerAfter = handlerTimeout,
            TimeProvider = clock,
            DefaultExceptionHandler = failures.Record,
        });
        var handlerGate = Gate.Create();
        evt.Subscribe(async _ =>
        {
            await handlerGate.Task;
            throw new InvalidOperationException("slow, backgrounded");
        });

        var raising = evt.RaiseAsync(7);
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);
        var failuresWhenTheRaiseReturned = failures.Count;
        handlerGate.SetResult();
        var routed = await Eventually.IsTrueAsync(() => failures.Count == 1);

        await Assert.That(failuresWhenTheRaiseReturned).IsEqualTo(0);
        await Assert.That(routed).IsTrue();
        var failure = failures.Failures.Single();
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.Handler);
        await Assert.That(failure.Call).IsTypeOf<EventCall<int>>();
        await evt.DisposeAsync();
    }

    [Test]
    public async Task CombinedAndCompositeHandlers_RouteEveryPartsFailure()
    {
        //every handler task is observed: Task.WhenAll faults and each part of a combined Subscribe(a + b) are routed separately.
        //The yielding part must finish before the raise returns, so the handler timeout runs on a clock that never moves
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions { DefaultExceptionHandler = failures.Record, TimeProvider = new FakeTimeProvider() });
        Func<EventCall<int>, Task> twoFaultsAtOnce = _ => Task.WhenAll(
            Task.FromException(new InvalidOperationException("when-all a")),
            Task.FromException(new InvalidOperationException("when-all b")));
        evt.Subscribe(twoFaultsAtOnce);
        Func<EventCall<int>, Task> partOne = _ => throw new InvalidOperationException("func part one");
        Func<EventCall<int>, Task> partTwo = async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("func part two");
        };
        evt.Subscribe(partOne + partTwo);
        Action<EventCall<int>> syncPartOne = _ => throw new InvalidOperationException("action part one");
        Action<EventCall<int>> syncPartTwo = _ => throw new InvalidOperationException("action part two");
        evt.Subscribe(syncPartOne + syncPartTwo);

        await evt.RaiseAsync(1);

        var messages = failures.Failures.Select(failure => failure.Exception.Message).ToArray();
        string[] expectedMessages =
        [
            "when-all a",
            "when-all b",
            "func part one",
            "func part two",
            "action part one",
            "action part two",
        ];
        await Assert.That(messages).IsEquivalentTo(expectedMessages);
    }

    [Test]
    public async Task HandlerThatHonoursCancellation_IsNotReportedAsFailed()
    {
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = null, DefaultExceptionHandler = failures.Record });
        var handlerWaiting = Gate.Create();
        evt.Subscribe(async call =>
        {
            handlerWaiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.CancellationToken);
        });
        using var raiseCancellation = new CancellationTokenSource();

        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);
        await handlerWaiting.Task.WaitAsync(Eventually.DefaultTimeout);
        await raiseCancellation.CancelAsync();
        var finished = await Eventually.CompletesAsync(raising);

        await Assert.That(finished).IsTrue();
        await Assert.That(failures.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OperationCanceledExceptionWithoutCancellation_IsReportedAsAFailure()
    {
        //only a handler whose call token was actually cancelled counts as cancelled
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions { DefaultExceptionHandler = failures.Record });
        Func<EventCall<int>, Task> cancelsOnItsOwn = _ => throw new OperationCanceledException("not requested by anyone");
        evt.Subscribe(cancelsOnItsOwn);

        await evt.RaiseAsync(1);

        await Assert.That(failures.Count).IsEqualTo(1);
        await Assert.That(failures.Failures[0].Exception).IsTypeOf<OperationCanceledException>();
    }

    [Test]
    public async Task HandlerReturningNull_IsTreatedAsFinished()
    {
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions { DefaultExceptionHandler = failures.Record });
        Func<EventCall<int>, Task> returnsNull = _ => null!;
        evt.Subscribe(returnsNull);

        var raising = evt.RaiseAsync(1);
        await raising;

        await Assert.That(raising.IsCompletedSuccessfully).IsTrue();
        await Assert.That(failures.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ClockThatCannotCreateTheHandlerTimeout_IsReportedAndTheHandlerIsStillObserved()
    {
        //the timeout was created outside any try: the raise faulted after a handler had started, the later handlers were
        //skipped, and the started handler's own failure became an unobserved task exception
        var failures = new FailureRecorder();
        var clock = new ThrowingClock(new FakeTimeProvider())
        {
            ThrowOnCreateTimer = true,
        };
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            DispatchMode = DispatchMode.Sequential,
            TimeProvider = clock,
            DefaultExceptionHandler = failures.Record,
        });
        var firstHandlerGate = Gate.Create();
        var secondHandlerCalls = 0;
        var firstSubscription = evt.Subscribe(async _ =>
        {
            await firstHandlerGate.Task;
            throw new ApplicationException("first handler failed after the raise started");
        });
        evt.Subscribe(_ =>
        {
            Interlocked.Increment(ref secondHandlerCalls);
        });

        var raising = evt.RaiseAsync(1);
        //without a timeout the raise waits for the handler, as with StartNextEventHandlerAfter = null
        var raiseWaitsForTheUntimedHandler = await Eventually.StaysIncompleteAsync(raising);
        firstHandlerGate.SetResult();
        var raised = await Eventually.CompletesAsync(raising);

        await Assert.That(raiseWaitsForTheUntimedHandler).IsTrue();
        await Assert.That(raised).IsTrue();
        await Assert.That(raising.IsCompletedSuccessfully).IsTrue();
        await Assert.That(Volatile.Read(ref secondHandlerCalls)).IsEqualTo(1);
        var dispatchFailures = failures.ForStage(EventFailureStage.Dispatch);
        await Assert.That(dispatchFailures).Count().IsEqualTo(1);
        await Assert.That(dispatchFailures[0].Exception.Message).IsEqualTo("timer creation failed");
        await Assert.That(dispatchFailures[0].SubscriptionId).IsEqualTo(firstSubscription.Id);
        await Assert.That(dispatchFailures[0].Call).IsNotNull();
        var handlerFailures = failures.ForStage(EventFailureStage.Handler);
        await Assert.That(handlerFailures).Count().IsEqualTo(1);
        await Assert.That(handlerFailures[0].Exception.Message).IsEqualTo("first handler failed after the raise started");
        await Assert.That(evt.BackgroundWorkCount).IsEqualTo(0);
    }

    [Test]
    public async Task ClockWhoseTimerFailsToDispose_IsReportedAndTheHandlerIsStillObserved()
    {
        //the timeout of a handler that finished first is stopped, which disposes the clock's timer inside Cancel()
        var failures = new FailureRecorder();
        var clock = new ThrowingClock(new FakeTimeProvider())
        {
            ThrowOnTimerDispose = true,
        };
        var evt = new IntercomEvent<int>(new EventingOptions { TimeProvider = clock, DefaultExceptionHandler = failures.Record });
        var handlerGate = Gate.Create();
        evt.Subscribe(async _ =>
        {
            await handlerGate.Task;
            throw new ApplicationException("handler failed before its timeout");
        });

        var raising = evt.RaiseAsync(1);
        handlerGate.SetResult();
        var raised = await Eventually.CompletesAsync(raising);

        await Assert.That(raised).IsTrue();
        await Assert.That(raising.IsCompletedSuccessfully).IsTrue();
        var dispatchFailures = failures.ForStage(EventFailureStage.Dispatch);
        await Assert.That(dispatchFailures).Count().IsEqualTo(1);
        await Assert.That(dispatchFailures[0].Exception.Message).IsEqualTo("timer disposal failed");
        var handlerFailures = failures.ForStage(EventFailureStage.Handler);
        await Assert.That(handlerFailures).Count().IsEqualTo(1);
        await Assert.That(handlerFailures[0].Exception.Message).IsEqualTo("handler failed before its timeout");
    }

    private static Task ThrowAsync(EventCall<int> call)
    {
        throw new InvalidOperationException($"handler failure for {call.Payload}");
    }
}
