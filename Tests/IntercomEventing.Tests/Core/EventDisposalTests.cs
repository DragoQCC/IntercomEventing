using IntercomEventing.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// <see cref="IntercomEventBase{TCall}.DisposeAsync"/>: idempotent, guards every later use, cancels handler tokens, waits for
/// background work, and never deadlocks when awaited from inside the event's own handlers or hooks.
/// </summary>
public class EventDisposalTests
{
    //the dispatcher waits StartNextEventHandlerAfter plus this documented grace before moving a handler to the background
    private static readonly TimeSpan DocumentedTimeoutGrace = TimeSpan.FromMilliseconds(50);

    [Test]
    public async Task DisposeAsync_IsIdempotentEvenWhileTheFirstCallIsStillRunning()
    {
        //A6: a second DisposeAsync threw ObjectDisposedException (SemaphoreSlim) in 1.2.0
        var evt = new IntercomEvent<int>(new EventingOptions());
        var hookGate = Gate.Create();
        var unsubscribeHookRuns = 0;
        await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = async _ =>
            {
                Interlocked.Increment(ref unsubscribeHookRuns);
                await hookGate.Task;
            },
        });

        var firstDispose = evt.DisposeAsync().AsTask();
        var secondDispose = evt.DisposeAsync().AsTask();
        var secondReturnedAtOnce = secondDispose.IsCompletedSuccessfully;
        var firstStillRunning = !firstDispose.IsCompleted;
        hookGate.SetResult();
        await firstDispose.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(secondReturnedAtOnce).IsTrue();
        await Assert.That(firstStillRunning).IsTrue();
        await Assert.That(Volatile.Read(ref unsubscribeHookRuns)).IsEqualTo(1);
        await Assert.That(evt.IsDisposed).IsTrue();
    }

    [Test]
    public async Task AfterDisposal_RaisingAndSubscribingThrowObjectDisposedException()
    {
        //A6: in 1.2.0 Subscribe after dispose was accepted silently
        var evt = new IntercomEvent<int>(new EventingOptions());
        await evt.DisposeAsync();
        Func<EventCall<int>, Task> handler = _ => Task.CompletedTask;

        await Assert.That(async () => await evt.RaiseAsync(1)).Throws<ObjectDisposedException>();
        await Assert.That(() => evt.Subscribe(handler)).Throws<ObjectDisposedException>();
        await Assert.That(() => evt.Subscribe(_ => { })).Throws<ObjectDisposedException>();
        await Assert.That(async () => await evt.SubscribeAsync(handler)).Throws<ObjectDisposedException>();
        await Assert.That(() => evt.Raised += handler).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task AfterDisposal_RemovingIsAHarmlessNoOp()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        Func<EventCall<int>, Task> handler = _ => Task.CompletedTask;
        var subscription = evt.Subscribe(handler);
        await evt.DisposeAsync();

        evt.Raised -= handler;
        var removedByUnsubscribe = evt.Unsubscribe(handler);
        subscription.Dispose();
        await subscription.DisposeAsync();

        await Assert.That(removedByUnsubscribe).IsFalse();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task RaiseAfterDisposal_ReportsTheFailureThroughTheReturnedTask()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        await evt.DisposeAsync();

        var raising = evt.RaiseAsync(1);

        await Assert.That(raising.IsFaulted).IsTrue();
        await Assert.That(raising.Exception!.InnerException).IsTypeOf<ObjectDisposedException>();
    }

    [Test]
    public async Task DisposeAsync_RemovesEverySubscriptionAndAwaitsTheirUnsubscribeHooks()
    {
        //sample section 10
        var evt = new IntercomEvent<string>(new EventingOptions());
        var unsubscribedHooks = new List<string>();
        var firstSubscription = await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = async _ =>
            {
                await Task.Yield();
                lock (unsubscribedHooks)
                {
                    unsubscribedHooks.Add("first");
                }
            },
        });
        var secondSubscription = await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = async _ =>
            {
                await Task.Yield();
                lock (unsubscribedHooks)
                {
                    unsubscribedHooks.Add("second");
                }
            },
        });

        await evt.DisposeAsync();

        await Assert.That(unsubscribedHooks).IsEquivalentTo(["first", "second"]);
        await Assert.That(firstSubscription.IsActive).IsFalse();
        await Assert.That(secondSubscription.IsActive).IsFalse();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task DisposeAsync_CancelsTheTokenOfRunningHandlers()
    {
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = null });
        var handlerWaiting = Gate.Create();
        var handlerSawCancellation = false;
        evt.Subscribe(async call =>
        {
            handlerWaiting.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, call.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref handlerSawCancellation, true);
            }
        });

        var raising = evt.RaiseAsync(1);
        await handlerWaiting.Task.WaitAsync(Eventually.DefaultTimeout);
        await evt.DisposeAsync();
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(Volatile.Read(ref handlerSawCancellation)).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_DoesNotWaitForARaiseThatIsStillDeliveringInline()
    {
        //documented: only handlers moved to the background are waited for; an inline handler sees the cancelled token instead
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = null });
        var handlerGate = Gate.Create();
        CancellationToken handlerToken = default;
        evt.Subscribe(async call =>
        {
            handlerToken = call.CancellationToken;
            await handlerGate.Task;
        });

        var raising = evt.RaiseAsync(1);
        var disposed = await Eventually.CompletesAsync(evt.DisposeAsync().AsTask());
        var raiseStillDelivering = !raising.IsCompleted;
        handlerGate.SetResult();
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(disposed).IsTrue();
        await Assert.That(raiseStillDelivering).IsTrue();
        await Assert.That(handlerToken.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_FromInsideABackgroundedHandler_SkipsItselfButWaitsForTheOthers()
    {
        //core-port fix: awaiting DisposeAsync inside a handler that was moved to the background used to wait for itself
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(30);
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = handlerTimeout, TimeProvider = clock });
        var disposingHandlerGate = Gate.Create();
        var otherHandlerGate = Gate.Create();
        var otherHandlerFinished = false;
        var disposeReturned = Gate.Create<bool>();
        evt.Subscribe(async _ =>
        {
            await disposingHandlerGate.Task;
            await evt.DisposeAsync();
            disposeReturned.TrySetResult(Volatile.Read(ref otherHandlerFinished));
        });
        evt.Subscribe(async _ =>
        {
            await otherHandlerGate.Task;
            Volatile.Write(ref otherHandlerFinished, true);
        });

        var raising = evt.RaiseAsync(1);
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);
        var handlersInTheBackground = evt.BackgroundWorkCount;
        disposingHandlerGate.SetResult();
        var disposalStarted = await Eventually.IsTrueAsync(() => evt.IsDisposed);
        var disposeStillWaitsForTheOtherHandler = await Eventually.StaysIncompleteAsync(disposeReturned.Task);
        otherHandlerGate.SetResult();
        var otherHandlerFinishedFirst = await disposeReturned.Task.WaitAsync(Eventually.DefaultTimeout);
        var drained = await Eventually.IsTrueAsync(() => evt.BackgroundWorkCount == 0);

        await Assert.That(handlersInTheBackground).IsEqualTo(2);
        await Assert.That(disposalStarted).IsTrue();
        await Assert.That(disposeStillWaitsForTheOtherHandler).IsTrue();
        await Assert.That(otherHandlerFinishedFirst).IsTrue();
        await Assert.That(drained).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_RunsAnUnsubscribeHookOnlyAfterTheBackgroundedHandlerOfItsSubscriptionFinished()
    {
        //documented order: wait for background handlers, then run the unsubscribe hooks. A hook that releases what the handler
        //uses (a connection, a cache) must never run while that handler is still working
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(30);
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = handlerTimeout, TimeProvider = clock });
        var handlerGate = Gate.Create();
        var handlerFinished = false;
        var hookSawHandlerStillRunning = Gate.Create<bool>();
        await evt.SubscribeAsync(async _ =>
        {
            await handlerGate.Task;
            Volatile.Write(ref handlerFinished, true);
        }, new()
        {
            OnUnsubscribed = _ =>
            {
                hookSawHandlerStillRunning.TrySetResult(!Volatile.Read(ref handlerFinished));
                return Task.CompletedTask;
            },
        });

        var raising = evt.RaiseAsync(1);
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);
        var handlersInTheBackground = evt.BackgroundWorkCount;
        var disposing = evt.DisposeAsync().AsTask();
        var hookWaitedForTheHandler = await Eventually.StaysIncompleteAsync(hookSawHandlerStillRunning.Task);
        handlerGate.SetResult();
        var disposed = await Eventually.CompletesAsync(disposing);
        var hookRanWhileTheHandlerRan = await hookSawHandlerStillRunning.Task.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(handlersInTheBackground).IsEqualTo(1);
        await Assert.That(hookWaitedForTheHandler).IsTrue();
        await Assert.That(disposed).IsTrue();
        await Assert.That(hookRanWhileTheHandlerRan).IsFalse();
    }

    [Test]
    public async Task DisposeAsync_AlsoWaitsForAHandlerThatMovesToTheBackgroundWhileDisposalIsWaiting()
    {
        //every subscription is removed before the wait, yet work can still join it: here a sequential raise that was delivering
        //inline when disposal started sees its second handler cross the timeout while disposal waits for the first
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(30);
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            DispatchMode = DispatchMode.Sequential,
            StartNextEventHandlerAfter = handlerTimeout,
            TimeProvider = clock,
        });
        var firstHandlerGate = Gate.Create();
        var secondHandlerGate = Gate.Create();
        var secondHandlerStarted = Gate.Create();
        var secondHandlerFinished = false;
        evt.Subscribe(async _ =>
        {
            await firstHandlerGate.Task;
        });
        evt.Subscribe(async _ =>
        {
            secondHandlerStarted.TrySetResult();
            await secondHandlerGate.Task;
            Volatile.Write(ref secondHandlerFinished, true);
        });

        var raising = evt.RaiseAsync(1);
        //the first handler moves to the background, so the raise starts the second one and waits for it inline
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await secondHandlerStarted.Task.WaitAsync(Eventually.DefaultTimeout);
        var disposing = evt.DisposeAsync().AsTask();
        //disposal is now waiting for the first handler, the only background work it has seen so far
        var disposalWaitsForTheFirstHandler = await Eventually.StaysIncompleteAsync(disposing);
        var secondHandlerMovedToTheBackground = await FakeClockStepping.AdvancePastHandlerTimeoutUntilAsync(clock, handlerTimeout,
            () => evt.BackgroundWorkCount == 2);
        firstHandlerGate.SetResult();
        var disposalWaitsForTheSecondHandler = await Eventually.StaysIncompleteAsync(disposing);
        secondHandlerGate.SetResult();
        var disposed = await Eventually.CompletesAsync(disposing);
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(disposalWaitsForTheFirstHandler).IsTrue();
        await Assert.That(secondHandlerMovedToTheBackground).IsTrue();
        await Assert.That(disposalWaitsForTheSecondHandler).IsTrue();
        await Assert.That(disposed).IsTrue();
        await Assert.That(Volatile.Read(ref secondHandlerFinished)).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_FromAnInlineHandler_Completes()
    {
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = null });
        var subscription = await evt.SubscribeAsync(async _ =>
        {
            await Task.Yield();
            await evt.DisposeAsync();
        }, new()
        {
            OnUnsubscribed = _ => Task.CompletedTask,
        });

        var raised = await Eventually.CompletesAsync(evt.RaiseAsync(1));

        await Assert.That(raised).IsTrue();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
        await Assert.That(subscription.IsActive).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeAsync_OfAnOuterEventFromANestedRaisesHandler_DoesNotDeadlock(bool innerEventHasATimeout)
    {
        //a handler of event B raised by a backgrounded handler of event A disposes A: disposal skips the enclosing handler too
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(30);
        var outer = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = handlerTimeout, TimeProvider = clock });
        TimeSpan? innerTimeout = innerEventHasATimeout ? handlerTimeout : null;
        var inner = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = innerTimeout, TimeProvider = clock });
        var outerHandlerGate = Gate.Create();
        var outerDisposedFromInnerHandler = Gate.Create();
        inner.Subscribe(async _ =>
        {
            await outer.DisposeAsync();
            outerDisposedFromInnerHandler.TrySetResult();
        });
        outer.Subscribe(async call =>
        {
            await outerHandlerGate.Task;
            await inner.RaiseAsync(call.Payload);
        });

        var raising = outer.RaiseAsync(1);
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);
        outerHandlerGate.SetResult();
        var returned = await Eventually.CompletesAsync(outerDisposedFromInnerHandler.Task);

        await Assert.That(returned).IsTrue();
        await inner.DisposeAsync().AsTask().WaitAsync(Eventually.DefaultTimeout);
    }

    [Test]
    public async Task DisposeAsync_FromABackgroundUnsubscribeHook_Completes()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var hookGate = Gate.Create();
        var disposedInsideTheHook = Gate.Create();
        var subscription = await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = async _ =>
            {
                await hookGate.Task;
                await evt.DisposeAsync();
                disposedInsideTheHook.TrySetResult();
            },
        });

        //Dispose never blocks, so it returns while the hook is still held at its gate. It runs off the test's own flow so that
        //a Dispose which blocked on the hook (the S7 defect) fails this test instead of hanging it before the gate opens
        var disposing = Task.Run(() => subscription.Dispose());
        var disposeReturnedWhileTheHookWaited = await Eventually.CompletesAsync(disposing);
        hookGate.SetResult();
        var returned = await Eventually.CompletesAsync(disposedInsideTheHook.Task);

        await Assert.That(disposeReturnedWhileTheHookWaited).IsTrue();
        await Assert.That(returned).IsTrue();
        await Assert.That(evt.IsDisposed).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_FromAnAbandonedSubscribeHook_CompletesAndTheHookIsUndone()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var hookGate = Gate.Create();
        var disposedInsideTheHook = Gate.Create();
        var undoHookRan = Gate.Create();
        using var subscribeCancellation = new CancellationTokenSource();
        var subscribing = evt.SubscribeAsync(_ => { }, new()
        {
            OnSubscribed = async _ =>
            {
                await hookGate.Task;
                await evt.DisposeAsync();
                disposedInsideTheHook.TrySetResult();
            },
            OnUnsubscribed = _ =>
            {
                undoHookRan.TrySetResult();
                return Task.CompletedTask;
            },
        }, subscribeCancellation.Token).AsTask();

        await subscribeCancellation.CancelAsync();
        await Assert.That(async () => await subscribing.WaitAsync(Eventually.DefaultTimeout)).Throws<OperationCanceledException>();
        hookGate.SetResult();
        var disposeReturned = await Eventually.CompletesAsync(disposedInsideTheHook.Task);
        var undone = await Eventually.CompletesAsync(undoHookRan.Task);

        await Assert.That(disposeReturned).IsTrue();
        await Assert.That(undone).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_FromWorkAHandlerStarted_DoesNotWaitForThatHandler()
    {
        //documented caveat: Task.Run inside a handler carries the handler's scope, so disposal there does not wait for it
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(20);
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = handlerTimeout, TimeProvider = clock });
        var startWorkGate = Gate.Create();
        var finishHandlerGate = Gate.Create();
        var disposeFromStartedWork = Gate.Create<Task>();
        evt.Subscribe(async _ =>
        {
            await startWorkGate.Task;
            disposeFromStartedWork.TrySetResult(Task.Run(async () => await evt.DisposeAsync()));
            await finishHandlerGate.Task;
        });

        var raising = evt.RaiseAsync(1);
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);
        startWorkGate.SetResult();
        var disposeTask = await disposeFromStartedWork.Task.WaitAsync(Eventually.DefaultTimeout);
        var returnedWhileTheHandlerStillRuns = await Eventually.CompletesAsync(disposeTask);
        finishHandlerGate.SetResult();

        await Assert.That(returnedWhileTheHandlerStillRuns).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_FromWorkStartedWithSuppressedFlow_WaitsForTheHandler()
    {
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(20);
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = handlerTimeout, TimeProvider = clock });
        var startWorkGate = Gate.Create();
        var finishHandlerGate = Gate.Create();
        var disposeFromStartedWork = Gate.Create<Task>();
        evt.Subscribe(async _ =>
        {
            await startWorkGate.Task;
            Task disposeTask;
            using (ExecutionContext.SuppressFlow())
            {
                disposeTask = Task.Run(async () => await evt.DisposeAsync());
            }
            disposeFromStartedWork.TrySetResult(disposeTask);
            await finishHandlerGate.Task;
        });

        var raising = evt.RaiseAsync(1);
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);
        startWorkGate.SetResult();
        var disposeTask = await disposeFromStartedWork.Task.WaitAsync(Eventually.DefaultTimeout);
        var disposeWaitsForTheHandler = await Eventually.StaysIncompleteAsync(disposeTask);
        finishHandlerGate.SetResult();
        var disposeFinished = await Eventually.CompletesAsync(disposeTask);

        await Assert.That(disposeWaitsForTheHandler).IsTrue();
        await Assert.That(disposeFinished).IsTrue();
    }

    [Test]
    public async Task DisposalFailures_AreRoutedAndDisposalStillCompletes()
    {
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = null, DefaultExceptionHandler = failures.Record });
        var callbackRegistered = Gate.Create();
        evt.Subscribe(async call =>
        {
            call.CancellationToken.Register(() => throw new ApplicationException("cancellation callback failure"));
            callbackRegistered.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.CancellationToken);
        });
        await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = _ => throw new ApplicationException("unsubscribe hook failure"),
        });
        using var raiseCancellation = new CancellationTokenSource();

        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);
        await callbackRegistered.Task.WaitAsync(Eventually.DefaultTimeout);
        var disposing = evt.DisposeAsync().AsTask();
        await disposing.WaitAsync(Eventually.DefaultTimeout);
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(disposing.IsCompletedSuccessfully).IsTrue();
        var disposalFailures = failures.ForStage(EventFailureStage.Disposal);
        var unsubscribeFailures = failures.ForStage(EventFailureStage.OnUnsubscribed);
        await Assert.That(disposalFailures).Count().IsEqualTo(1);
        await Assert.That(disposalFailures[0].Exception.Message).IsEqualTo("cancellation callback failure");
        await Assert.That(unsubscribeFailures).Count().IsEqualTo(1);
        //the handler that ended with the cancelled token honoured cancellation, so it is not a handler failure
        await Assert.That(failures.ForStage(EventFailureStage.Handler)).IsEmpty();
    }
}
