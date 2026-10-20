using System.Runtime.CompilerServices;
using IntercomEventing.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// Every failure path observes its tasks: after the scenarios below run and the garbage collector finalizes their tasks,
/// <see cref="TaskScheduler.UnobservedTaskException"/> has fired zero times (the judges' harness criterion).
/// </summary>
/// <remarks>
/// The event is process-wide, so this test runs on its own and first drains whatever earlier tests left for the finalizer.
/// </remarks>
[NotInParallel]
public class UnobservedTaskExceptionTests
{
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromMilliseconds(50);

    //the dispatcher waits StartNextEventHandlerAfter plus this documented grace before moving a handler to the background
    private static readonly TimeSpan DocumentedTimeoutGrace = TimeSpan.FromMilliseconds(50);

    [Test]
    public async Task FailureScenarios_LeaveNoUnobservedTaskExceptions()
    {
        //S1, S5, S9, S13: 1.2.0 surfaced handler, factory and loop failures only through UnobservedTaskException
        await CollectAndFinalizeAsync();
        var unobservedExceptions = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> countUnobserved = (_, arguments) =>
        {
            lock (unobservedExceptions)
            {
                unobservedExceptions.Add(arguments.Exception);
            }
            arguments.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += countUnobserved;
        try
        {
            await RunEveryFailureScenarioAsync();
            await CollectAndFinalizeAsync();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= countUnobserved;
        }

        await Assert.That(unobservedExceptions).IsEmpty();
    }

    //out of line so the scenarios' tasks are unreachable when the caller collects
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunEveryFailureScenarioAsync()
    {
        await RaiseFaultingHandlersAsync(DispatchMode.Parallel);
        await RaiseFaultingHandlersAsync(DispatchMode.Sequential);
        await FailInTheBackgroundAsync();
        await FailInsideExceptionHandlersAsync();
        await FailInSubscriptionHooksAsync();
        await FailInAbandonedSubscribeHookAsync();
        await FailDuringDisposalAsync();
        await RaiseAfterDisposalAsync();
        await FailTimedScheduleAsync();
        await FailDebounceWaitAsync();
        await CancelHandlersAsync();
    }

    private static async Task CollectAndFinalizeAsync()
    {
        for (var collection = 0; collection < 3; collection++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(20);
        }
    }

    private static async Task RaiseFaultingHandlersAsync(DispatchMode dispatchMode)
    {
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions { DispatchMode = dispatchMode, DefaultExceptionHandler = failures.Record });
        evt.Subscribe(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("after yield");
        });
        Func<EventCall<int>, Task> throwsSynchronously = _ => throw new InvalidOperationException("synchronous");
        evt.Subscribe(throwsSynchronously);
        Func<EventCall<int>, Task> twoFaultsAtOnce = _ => Task.WhenAll(
            Task.FromException(new InvalidOperationException("a")),
            Task.FromException(new InvalidOperationException("b")));
        evt.Subscribe(twoFaultsAtOnce);
        Func<EventCall<int>, Task> partOne = _ => throw new InvalidOperationException("part one");
        Func<EventCall<int>, Task> partTwo = async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("part two");
        };
        evt.Subscribe(partOne + partTwo);
        using var raiseCancellation = new CancellationTokenSource();
        await evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);
        await evt.DisposeAsync();
    }

    private static async Task FailInTheBackgroundAsync()
    {
        var clock = new FakeTimeProvider();
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            StartNextEventHandlerAfter = HandlerTimeout,
            TimeProvider = clock,
            DefaultExceptionHandler = failures.Record,
        });
        var handlerGate = Gate.Create();
        evt.Subscribe(async _ =>
        {
            await handlerGate.Task;
            throw new InvalidOperationException("slow handler failure");
        });
        //a raiser token makes the raise own a linked token source, released by a continuation once the slow handler ends
        using var raiseCancellation = new CancellationTokenSource();
        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);
        clock.Advance(HandlerTimeout + DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);
        handlerGate.SetResult();
        await evt.DisposeAsync().AsTask().WaitAsync(Eventually.DefaultTimeout);
    }

    private static async Task FailInsideExceptionHandlersAsync()
    {
        var clock = new FakeTimeProvider();
        var evt = new IntercomEvent<int>(new EventingOptions
        {
            StartNextEventHandlerAfter = HandlerTimeout,
            TimeProvider = clock,
            DefaultExceptionHandler = _ => throw new ApplicationException("default handler fault"),
        });
        Func<EventCall<int>, Task> throwing = _ => throw new InvalidOperationException("handler");
        evt.Subscribe(throwing, (_, _) => throw new ApplicationException("subscription handler fault"));
        evt.Subscribe(throwing);
        var handlerGate = Gate.Create();
        evt.Subscribe(async _ =>
        {
            await handlerGate.Task;
            throw new InvalidOperationException("backgrounded handler");
        });
        var raising = evt.RaiseAsync(1);
        clock.Advance(HandlerTimeout + DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);
        handlerGate.SetResult();
        await evt.DisposeAsync().AsTask().WaitAsync(Eventually.DefaultTimeout);
    }

    private static async Task FailInSubscriptionHooksAsync()
    {
        var failures = new FailureRecorder();
        var evt = new IntercomEvent<int>(new EventingOptions { DefaultExceptionHandler = failures.Record });
        try
        {
            await evt.SubscribeAsync(_ => { }, new()
            {
                OnSubscribed = async _ =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("subscribe hook failure");
                },
            });
        }
        catch (InvalidOperationException)
        {
            //expected: the caller of SubscribeAsync receives the hook's failure
        }
        var backgroundHookSubscription = await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("background unsubscribe hook failure");
            },
        });
        backgroundHookSubscription.Dispose();
        var awaitedHookSubscription = await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = _ => throw new InvalidOperationException("awaited unsubscribe hook failure"),
        });
        try
        {
            await awaitedHookSubscription.DisposeAsync();
        }
        catch (InvalidOperationException)
        {
            //expected: an awaited unsubscribe hook's failure reaches whoever awaited it
        }
        await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = _ => throw new InvalidOperationException("disposal unsubscribe hook failure"),
        });
        await evt.DisposeAsync();
    }

    private static async Task FailInAbandonedSubscribeHookAsync()
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
                throw new InvalidOperationException("abandoned hook failure");
            },
        }, subscribeCancellation.Token).AsTask();
        await subscribeCancellation.CancelAsync();
        try
        {
            await subscribing;
        }
        catch (OperationCanceledException)
        {
            //expected: the caller stopped waiting
        }
        hookGate.SetResult();
        await evt.DisposeAsync();
    }

    private static async Task FailDuringDisposalAsync()
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
        using var raiseCancellation = new CancellationTokenSource();
        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);
        await callbackRegistered.Task.WaitAsync(Eventually.DefaultTimeout);
        await evt.DisposeAsync();
        await raising.WaitAsync(Eventually.DefaultTimeout);
    }

    private static async Task RaiseAfterDisposalAsync()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        await evt.DisposeAsync();
        try
        {
            await evt.RaiseAsync(1);
        }
        catch (ObjectDisposedException)
        {
            //expected: reported through the returned task
        }
    }

    private static async Task FailTimedScheduleAsync()
    {
        var fakeClock = new FakeTimeProvider();
        var clock = new ThrowingClock(fakeClock);
        var failures = new FailureRecorder();
        var interval = TimeSpan.FromSeconds(1);
        var timer = new TimedEvent(interval, options: new EventingOptions { TimeProvider = clock, DefaultExceptionHandler = failures.Record });
        var ticks = 0;
        timer.Subscribe(_ =>
        {
            Interlocked.Increment(ref ticks);
            throw new InvalidOperationException("tick handler failure");
        });
        timer.Start();
        fakeClock.Advance(interval);
        await Eventually.IsTrueAsync(() => Volatile.Read(ref ticks) == 1);
        clock.ThrowOnGetUtcNow = true;
        fakeClock.Advance(interval);
        await Eventually.IsTrueAsync(() => !timer.IsRunning);
        clock.ThrowOnGetUtcNow = false;
        await timer.DisposeAsync();
    }

    private static async Task FailDebounceWaitAsync()
    {
        var fakeClock = new FakeTimeProvider();
        var clock = new ThrowingClock(fakeClock);
        var failures = new FailureRecorder();
        var interval = TimeSpan.FromSeconds(1);
        var debounce = new DebounceEvent<int>(interval, new EventingOptions { TimeProvider = clock, DefaultExceptionHandler = failures.Record });
        debounce.Subscribe(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("debounce handler failure");
        });
        debounce.Trigger(1);
        fakeClock.Advance(interval);
        await Eventually.IsTrueAsync(() => failures.Count == 1);
        clock.ThrowOnGetUtcNow = true;
        debounce.Trigger(2);
        fakeClock.Advance(interval);
        await Eventually.IsTrueAsync(() => failures.Count == 2);
        clock.ThrowOnGetUtcNow = false;
        await debounce.DisposeAsync();
    }

    private static async Task CancelHandlersAsync()
    {
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = TimeSpan.FromMilliseconds(5) });
        evt.Subscribe(async call => await Task.Delay(Timeout.Infinite, call.CancellationToken));
        using var raiseCancellation = new CancellationTokenSource();
        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);
        await raiseCancellation.CancelAsync();
        await raising.WaitAsync(Eventually.DefaultTimeout);
        var secondRaise = evt.RaiseAsync(2);
        await Task.Delay(30);
        await evt.DisposeAsync();
        await secondRaise.WaitAsync(Eventually.DefaultTimeout);
    }
}
