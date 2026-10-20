using System.Reflection;
using System.Runtime.CompilerServices;
using IntercomEventing.Tests.TestSupport;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// <see cref="SubscriptionOptions{TCall}"/> hooks: <c>OnSubscribed</c> finishes before the subscription can receive calls and
/// leaves nothing behind when it fails, and <c>OnUnsubscribed</c> runs exactly once, awaited or tracked in the background.
/// </summary>
public class SubscriptionHookTests
{
    [Test]
    public async Task OnSubscribed_FinishesBeforeTheSubscriptionReceivesCalls()
    {
        //A7 and the sample's OnSubscribed gate: a raise during the hook does not reach the handler
        var evt = new IntercomEvent<int>(new EventingOptions());
        var calls = 0;
        var hookGate = Gate.Create();
        var handleActiveDuringTheHook = true;
        var subscribing = evt.SubscribeAsync(_ =>
        {
            Interlocked.Increment(ref calls);
        }, new()
        {
            OnSubscribed = handle =>
            {
                handleActiveDuringTheHook = handle.IsActive;
                return hookGate.Task;
            },
        }).AsTask();

        await evt.RaiseAsync(1);
        var countWhileTheHookRuns = evt.SubscriberCount;
        var callsWhileTheHookRuns = Volatile.Read(ref calls);
        hookGate.SetResult();
        var subscription = await subscribing.WaitAsync(Eventually.DefaultTimeout);
        await evt.RaiseAsync(2);

        await Assert.That(handleActiveDuringTheHook).IsFalse();
        await Assert.That(countWhileTheHookRuns).IsEqualTo(0);
        await Assert.That(callsWhileTheHookRuns).IsEqualTo(0);
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(1);
        await Assert.That(subscription.IsActive).IsTrue();
    }

    [Test]
    public async Task OnSubscribed_ThatThrows_LeavesNothingRegisteredAndReachesTheCaller()
    {
        //A7: in 1.2.0 the subscription stayed registered, kept receiving events, and the caller never got a handle to remove it
        var evt = new IntercomEvent<int>(new EventingOptions());
        var calls = 0;
        var unsubscribeHookRuns = 0;
        var hookGate = Gate.Create();
        var subscribing = evt.SubscribeAsync(_ =>
        {
            Interlocked.Increment(ref calls);
        }, new()
        {
            OnSubscribed = async _ =>
            {
                await hookGate.Task;
                throw new InvalidOperationException("warm-up failed");
            },
            OnUnsubscribed = _ =>
            {
                Interlocked.Increment(ref unsubscribeHookRuns);
                return Task.CompletedTask;
            },
        }).AsTask();

        await evt.RaiseAsync(1);
        hookGate.SetResult();
        var thrown = await Assert.That(async () => await subscribing.WaitAsync(Eventually.DefaultTimeout)).Throws<InvalidOperationException>();
        await evt.RaiseAsync(2);

        await Assert.That(thrown!.Message).IsEqualTo("warm-up failed");
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(0);
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
        await Assert.That(Volatile.Read(ref unsubscribeHookRuns)).IsEqualTo(0);
    }

    [Test]
    public async Task OnSubscribed_ThatThrowsSynchronously_LeavesNothingRegistered()
    {
        //sample section 2: a hook that throws before returning a task behaves like one whose task faults
        var evt = new IntercomEvent<int>(new EventingOptions());

        var subscribing = evt.SubscribeAsync(_ => { }, new()
        {
            OnSubscribed = _ => throw new InvalidOperationException("warm-up failed"),
        }).AsTask();

        await Assert.That(async () => await subscribing).Throws<InvalidOperationException>();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnSubscribed_ThatIsCancelled_LeavesNothingRegistered()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());

        var subscribing = evt.SubscribeAsync(_ => { }, new()
        {
            OnSubscribed = _ => Task.FromCanceled(new CancellationToken(canceled: true)),
        }).AsTask();

        await Assert.That(async () => await subscribing).Throws<OperationCanceledException>();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task AlreadyCancelledToken_FailsBeforeTheHookRuns()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var hookRuns = 0;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var subscribing = evt.SubscribeAsync(_ => { }, new()
        {
            OnSubscribed = _ =>
            {
                Interlocked.Increment(ref hookRuns);
                return Task.CompletedTask;
            },
        }, cancelled.Token).AsTask();

        await Assert.That(async () => await subscribing).Throws<OperationCanceledException>();
        await Assert.That(Volatile.Read(ref hookRuns)).IsEqualTo(0);
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task CancellingTheWait_UndoesTheHookOnceItLaterSucceeds()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var hookGate = Gate.Create();
        var undoHookRan = Gate.Create();
        using var subscribeCancellation = new CancellationTokenSource();
        var subscribing = evt.SubscribeAsync(_ => { }, new()
        {
            OnSubscribed = _ => hookGate.Task,
            OnUnsubscribed = _ =>
            {
                undoHookRan.TrySetResult();
                return Task.CompletedTask;
            },
        }, subscribeCancellation.Token).AsTask();

        await subscribeCancellation.CancelAsync();
        await Assert.That(async () => await subscribing.WaitAsync(Eventually.DefaultTimeout)).Throws<OperationCanceledException>();
        var undoneBeforeTheHookFinished = undoHookRan.Task.IsCompleted;
        hookGate.SetResult();
        var undone = await Eventually.CompletesAsync(undoHookRan.Task);

        await Assert.That(undoneBeforeTheHookFinished).IsFalse();
        await Assert.That(undone).IsTrue();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
        await evt.DisposeAsync().AsTask().WaitAsync(Eventually.DefaultTimeout);
    }

    [Test]
    public async Task CancellingTheWait_RoutesALaterHookFailureAsOnSubscribed()
    {
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions { DefaultExceptionHandler = failures.Record });
        var hookGate = Gate.Create();
        using var subscribeCancellation = new CancellationTokenSource();
        var subscribing = evt.SubscribeAsync(_ => { }, new()
        {
            OnSubscribed = async _ =>
            {
                await hookGate.Task;
                throw new InvalidOperationException("late hook failure");
            },
        }, subscribeCancellation.Token).AsTask();

        await subscribeCancellation.CancelAsync();
        await Assert.That(async () => await subscribing.WaitAsync(Eventually.DefaultTimeout)).Throws<OperationCanceledException>();
        hookGate.SetResult();
        var routed = await Eventually.IsTrueAsync(() => failures.Count == 1);

        await Assert.That(routed).IsTrue();
        var failure = failures.Failures.Single();
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.OnSubscribed);
        await Assert.That(failure.Call).IsNull();
        await Assert.That(failure.SubscriptionId).IsNotNull();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleDisposedInsideOnSubscribed_NeverBecomesActive()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var calls = 0;
        var undoHookRuns = 0;
        var subscription = await evt.SubscribeAsync(_ =>
        {
            Interlocked.Increment(ref calls);
        }, new()
        {
            OnSubscribed = async handle =>
            {
                handle.Dispose();
                await Task.Yield();
            },
            OnUnsubscribed = _ =>
            {
                Interlocked.Increment(ref undoHookRuns);
                return Task.CompletedTask;
            },
        });

        await evt.RaiseAsync(1);

        await Assert.That(subscription.IsActive).IsFalse();
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(0);
        await Assert.That(Volatile.Read(ref undoHookRuns)).IsEqualTo(1);
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task EventDisposedWhileOnSubscribedRuns_SubscribeAsyncThrowsAndUndoesTheHook()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var hookGate = Gate.Create();
        var undoHookRuns = 0;
        var subscribing = evt.SubscribeAsync(_ => { }, new()
        {
            OnSubscribed = _ => hookGate.Task,
            OnUnsubscribed = _ =>
            {
                Interlocked.Increment(ref undoHookRuns);
                return Task.CompletedTask;
            },
        }).AsTask();

        var disposing = evt.DisposeAsync().AsTask();
        var disposeDidNotWaitForThePendingHook = await Eventually.CompletesAsync(disposing);
        hookGate.SetResult();

        await Assert.That(disposeDidNotWaitForThePendingHook).IsTrue();
        await Assert.That(async () => await subscribing.WaitAsync(Eventually.DefaultTimeout)).Throws<ObjectDisposedException>();
        await Assert.That(Volatile.Read(ref undoHookRuns)).IsEqualTo(1);
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task TwoHookedSubscribersOnASingleSubscriberEvent_OneWinsAndTheLoserIsUndone()
    {
        var evt = new IntercomEvent<int>(new EventingOptions { AllowMultipleSubscribers = false });
        var hookGate = Gate.Create();
        var undoHookRuns = 0;
        SubscriptionOptions<EventCall<int>> CreateHookedOptions()
        {
            return new SubscriptionOptions<EventCall<int>>
            {
                OnSubscribed = _ => hookGate.Task,
                OnUnsubscribed = _ =>
                {
                    Interlocked.Increment(ref undoHookRuns);
                    return Task.CompletedTask;
                },
            };
        }
        var firstSubscribing = evt.SubscribeAsync(_ => { }, CreateHookedOptions()).AsTask();
        var secondSubscribing = evt.SubscribeAsync(_ => { }, CreateHookedOptions()).AsTask();

        hookGate.SetResult();
        var outcomes = await Task.WhenAll(CaptureFailureAsync(firstSubscribing), CaptureFailureAsync(secondSubscribing))
            .WaitAsync(Eventually.DefaultTimeout);

        var winners = outcomes.Count(failure => failure is null);
        var losers = outcomes.Count(failure => failure is InvalidOperationException);
        await Assert.That(winners).IsEqualTo(1);
        await Assert.That(losers).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref undoHookRuns)).IsEqualTo(1);
        await Assert.That(evt.SubscriberCount).IsEqualTo(1);
    }

    [Test]
    public async Task RejectedSubscriptionWithoutOnSubscribed_NeverRunsOnUnsubscribed()
    {
        //OnUnsubscribed must not run for a subscription that never existed
        var unsubscribeHookRuns = 0;
        SubscriptionOptions<EventCall<int>> options = new()
        {
            OnUnsubscribed = _ =>
            {
                Interlocked.Increment(ref unsubscribeHookRuns);
                return Task.CompletedTask;
            },
        };
        for (var trial = 0; trial < 200; trial++)
        {
            var evt = new IntercomEvent<int>(new EventingOptions { AllowMultipleSubscribers = false });
            var attempts = Enumerable.Range(0, 4)
                .Select(attempt => Task.Run(() => CaptureFailureAsync(evt.SubscribeAsync(_ => { }, options).AsTask())))
                .ToArray();
            await Task.WhenAll(attempts).WaitAsync(Eventually.DefaultTimeout);
        }

        await Assert.That(Volatile.Read(ref unsubscribeHookRuns)).IsEqualTo(0);
    }

    [Test]
    public async Task SingleSubscriberEventWithASubscriber_RejectsSubscribeAsyncBeforeRunningTheHook()
    {
        var evt = new IntercomEvent<int>(new EventingOptions { AllowMultipleSubscribers = false });
        evt.Subscribe(_ => { });
        var hookRuns = 0;

        var subscribing = evt.SubscribeAsync(_ => { }, new()
        {
            OnSubscribed = _ =>
            {
                Interlocked.Increment(ref hookRuns);
                return Task.CompletedTask;
            },
        }).AsTask();

        await Assert.That(async () => await subscribing).Throws<InvalidOperationException>();
        await Assert.That(Volatile.Read(ref hookRuns)).IsEqualTo(0);
    }

    [Test]
    public async Task OnUnsubscribed_IsAwaitedByDisposeAsync()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var hookGate = Gate.Create();
        var subscription = await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = _ => hookGate.Task,
        });

        var unsubscribing = subscription.DisposeAsync().AsTask();
        var removedBeforeTheHookFinished = !subscription.IsActive && evt.SubscriberCount == 0;
        var waitedForTheHook = !unsubscribing.IsCompleted;
        hookGate.SetResult();
        await unsubscribing.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(removedBeforeTheHookFinished).IsTrue();
        await Assert.That(waitedForTheHook).IsTrue();
    }

    [Test]
    public async Task OnUnsubscribed_ThatThrows_ReachesTheAwaitingCallerAfterTheRemoval()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var subscription = await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = _ => throw new InvalidOperationException("flush failed"),
        });

        await Assert.That(async () => await subscription.UnsubscribeAsync()).Throws<InvalidOperationException>();
        await Assert.That(subscription.IsActive).IsFalse();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnUnsubscribed_StartedByMinusEquals_RunsInTheBackgroundAndDisposalWaitsForIt()
    {
        //sample section 2: -= cannot await, so the hook runs in the background and is tracked
        var evt = new IntercomEvent<int>(new EventingOptions());
        var hookGate = Gate.Create();
        var hookFinished = false;
        Func<EventCall<int>, Task> handler = _ => Task.CompletedTask;
        await evt.SubscribeAsync(handler, new()
        {
            OnUnsubscribed = async _ =>
            {
                await hookGate.Task;
                Volatile.Write(ref hookFinished, true);
            },
        });

        evt.Raised -= handler;
        var trackedInTheBackground = evt.BackgroundWorkCount;
        var disposing = evt.DisposeAsync().AsTask();
        var disposeWaitedForTheHook = !disposing.IsCompleted;
        hookGate.SetResult();
        await disposing.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(trackedInTheBackground).IsEqualTo(1);
        await Assert.That(disposeWaitedForTheHook).IsTrue();
        await Assert.That(Volatile.Read(ref hookFinished)).IsTrue();
    }

    [Test]
    public async Task OnUnsubscribed_StartedBySynchronousDispose_ReportsItsFailureWithoutACall()
    {
        //sample section 2: the global handler gets an OnUnsubscribed failure that names the subscription
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions { DefaultExceptionHandler = failures.Record });
        var subscription = await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = _ => throw new InvalidOperationException("unsubscribe hook failed"),
        });

        subscription.Dispose();
        var routed = await Eventually.IsTrueAsync(() => failures.Count == 1);

        await Assert.That(routed).IsTrue();
        var failure = failures.Failures.Single();
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.OnUnsubscribed);
        await Assert.That(failure.SubscriptionId).IsEqualTo(subscription.Id);
        await Assert.That(failure.Call).IsNull();
    }

    [Test]
    public async Task OnUnsubscribed_RunsExactlyOnceWhateverRemovesTheSubscription()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var unsubscribeHookRuns = 0;
        Func<EventCall<int>, Task> handler = _ => Task.CompletedTask;
        var subscription = await evt.SubscribeAsync(handler, new()
        {
            OnUnsubscribed = _ =>
            {
                Interlocked.Increment(ref unsubscribeHookRuns);
                return Task.CompletedTask;
            },
        });

        subscription.Dispose();
        await subscription.DisposeAsync();
        await subscription.UnsubscribeAsync();
        evt.Raised -= handler;
        await evt.DisposeAsync();

        await Assert.That(Volatile.Read(ref unsubscribeHookRuns)).IsEqualTo(1);
    }

    [Test]
    public async Task HooksReturningNull_AreTreatedAsFinished()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());

        var subscription = await evt.SubscribeAsync(_ => { }, new()
        {
            OnSubscribed = _ => null!,
            OnUnsubscribed = _ => null!,
        });
        var activeAfterSubscribing = subscription.IsActive;
        await subscription.DisposeAsync();

        await Assert.That(activeAfterSubscribing).IsTrue();
        await Assert.That(subscription.IsActive).IsFalse();
    }

    [Test]
    public async Task SynchronousDisposeWithAnAsyncHook_DoesNotBlockASingleThreadedContext()
    {
        //S7: 1.2.0's Dispose blocked on the async hook (sync-over-async) and deadlocked on a UI or WebAssembly thread
        var completed = await SingleThreadedContext.RunAsync(async () =>
        {
            var evt = new IntercomEvent<int>(new EventingOptions());
            var subscription = await evt.SubscribeAsync(_ => { }, new()
            {
                //Task.Yield posts back to the single thread, so a blocking Dispose would wait for itself
                OnUnsubscribed = async _ => await Task.Yield(),
            });
            subscription.Dispose();
            await evt.RaiseAsync(1);
            var second = await evt.SubscribeAsync(_ => { }, new()
            {
                OnUnsubscribed = async _ => await Task.Yield(),
            });
            await second.DisposeAsync();
            await evt.DisposeAsync();
        }, Eventually.DefaultTimeout);

        await Assert.That(completed).IsTrue();
    }

    [Test]
    public async Task EventSubscription_HasNoFinalizer()
    {
        //S7: 1.2.0's finalizer ran user hooks on the finalizer thread and could crash the process
        var finalizer = typeof(EventSubscription<EventCall<int>>).GetMethod(
            "Finalize",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        await Assert.That(finalizer).IsNull();
    }

    [Test]
    public async Task AbandonedSubscription_DoesNotRunItsHookWhenCollected()
    {
        //S7: an event and subscription nobody disposes are simply collected, like a native .NET event
        var hookRuns = new StrongBox<int>(0);
        var collectedEvent = await SubscribeAndAbandonAsync(hookRuns);

        for (var collection = 0; collection < 3; collection++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        await Assert.That(collectedEvent.IsAlive).IsFalse();
        await Assert.That(Volatile.Read(ref hookRuns.Value)).IsEqualTo(0);
    }

    //kept out of line so no local of the caller keeps the event alive
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> SubscribeAndAbandonAsync(StrongBox<int> hookRuns)
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = _ =>
            {
                Interlocked.Increment(ref hookRuns.Value);
                return Task.CompletedTask;
            },
        });
        return new WeakReference(evt);
    }

    private static async Task<Exception?> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
