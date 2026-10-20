using IntercomEventing.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// The protected contract for building a new event kind on <see cref="IntercomEventBase{TCall}"/>: <c>DispatchAsync</c>,
/// <c>RouteBackgroundRaiseFailure</c>, <c>StartWithoutCallerContext</c>, <c>OnDisposingAsync</c> and <c>DisposalToken</c>. The public <c>Options</c> that event
/// kinds also read is covered by <see cref="EventingOptionsTests"/>.
/// </summary>
public class CustomEventKindTests
{
    private static readonly TimeSpan DocumentedTimeoutGrace = TimeSpan.FromMilliseconds(50);

    //set by a test's own flow to see whether background work started from it still carries the value
    private static readonly AsyncLocal<string?> s_callerValue = new();

    [Test]
    public async Task DispatchAsync_WithoutSubscribers_NeverCallsTheFactory()
    {
        var evt = new ProbeEvent(new EventingOptions());

        await evt.RaiseAsync(1);

        await Assert.That(evt.FactoryInvocations).IsEqualTo(0);
    }

    [Test]
    public async Task DispatchAsync_CallsTheFactoryOncePerRaiseWhateverTheSubscriberCount()
    {
        //one raise means one call, shared by every handler
        var evt = new ProbeEvent(new EventingOptions());
        for (var handlerNumber = 0; handlerNumber < 3; handlerNumber++)
        {
            evt.Subscribe(_ => { });
        }

        await evt.RaiseAsync(1);
        await evt.RaiseAsync(2);

        await Assert.That(evt.FactoryInvocations).IsEqualTo(2);
    }

    [Test]
    public async Task DispatchAsync_WithANullFactory_ThrowsSynchronously()
    {
        var evt = new ProbeEvent(new EventingOptions());

        await Assert.That(() => evt.RaiseWithFactoryAsync(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task DispatchAsync_FailingFactory_FaultsTheReturnedTask()
    {
        //S5/S13: a failing CreateEventCall was swallowed in 1.2.0; the raiser now sees it like any other raise failure
        var evt = new ProbeEvent(new EventingOptions());
        var handlerCalls = 0;
        evt.Subscribe(_ =>
        {
            Interlocked.Increment(ref handlerCalls);
        });

        var raising = evt.RaiseWithFactoryAsync(static (_, _, _) => throw new InvalidOperationException("factory failure"));

        await Assert.That(raising.IsFaulted).IsTrue();
        await Assert.That(raising.Exception!.InnerException!.Message).IsEqualTo("factory failure");
        await Assert.That(Volatile.Read(ref handlerCalls)).IsEqualTo(0);
    }

    [Test]
    public async Task RouteBackgroundRaiseFailure_ReportsABackgroundRaiseFailure()
    {
        var failures = new FailureRecorder();
        var evt = new ProbeEvent(new EventingOptions { DefaultExceptionHandler = failures.Record }) { Name = "PollingEvent" };
        var loopFailure = new InvalidOperationException("sensor offline");

        evt.ReportBackgroundFailure(loopFailure);

        var failure = failures.Failures.Single();
        await Assert.That(failure.Stage).IsEqualTo(EventFailureStage.BackgroundRaise);
        await Assert.That(failure.Exception).IsSameReferenceAs(loopFailure);
        await Assert.That(failure.EventName).IsEqualTo("PollingEvent");
        await Assert.That(failure.SubscriptionId).IsNull();
        await Assert.That(failure.Call).IsNull();
    }

    [Test]
    public async Task RouteBackgroundRaiseFailure_ContainsAThrowingDefaultHandlerAndRejectsNull()
    {
        var evt = new ProbeEvent(new EventingOptions { DefaultExceptionHandler = _ => throw new ApplicationException("handler fault") });

        await Assert.That(() => evt.ReportBackgroundFailure(new InvalidOperationException("loop failure"))).ThrowsNothing();
        await Assert.That(() => evt.ReportBackgroundFailure(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task OnDisposingAsync_RunsOnceAfterTheDisposalTokenIsCancelledAndBeforeSubscriptionsAreRemoved()
    {
        var disposingRuns = 0;
        var tokenCancelledWhenDisposing = false;
        var subscribersWhenDisposing = -1;
        var evt = new ProbeEvent(new EventingOptions())
        {
            OnDisposing = probe =>
            {
                Interlocked.Increment(ref disposingRuns);
                tokenCancelledWhenDisposing = probe.ExposedDisposalToken.IsCancellationRequested;
                subscribersWhenDisposing = probe.SubscriberCount;
                return ValueTask.CompletedTask;
            },
        };
        evt.Subscribe(_ => { });

        await evt.DisposeAsync();
        await evt.DisposeAsync();

        await Assert.That(Volatile.Read(ref disposingRuns)).IsEqualTo(1);
        await Assert.That(tokenCancelledWhenDisposing).IsTrue();
        await Assert.That(subscribersWhenDisposing).IsEqualTo(1);
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnDisposingAsync_Failure_IsRoutedAsDisposalAndDisposalContinues()
    {
        var failures = new FailureRecorder();
        var unsubscribeHookRan = false;
        var evt = new ProbeEvent(new EventingOptions { DefaultExceptionHandler = failures.Record })
        {
            OnDisposing = _ => throw new InvalidOperationException("shutdown failure"),
        };
        await evt.SubscribeAsync(_ => { }, new()
        {
            OnUnsubscribed = _ =>
            {
                unsubscribeHookRan = true;
                return Task.CompletedTask;
            },
        });

        await evt.DisposeAsync();

        var disposalFailure = failures.ForStage(EventFailureStage.Disposal).Single();
        await Assert.That(disposalFailure.Exception.Message).IsEqualTo("shutdown failure");
        await Assert.That(unsubscribeHookRan).IsTrue();
        await Assert.That(evt.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task StartWithoutCallerContext_FromInsideAHandler_DisposalFromTheWorkStillWaitsForThatHandler()
    {
        //a custom kind's loop started from a handler must not count as part of that handler: disposal called from the loop
        //waits for the handler, as it does for TimedEvent (compare DisposeAsync_FromWorkStartedWithSuppressedFlow_WaitsForTheHandler)
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(20);
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = handlerTimeout, TimeProvider = clock });
        await using var customKind = new ProbeEvent(new EventingOptions());
        var startWorkGate = Gate.Create();
        var finishHandlerGate = Gate.Create();
        var disposeFromStartedWork = Gate.Create<Task>();
        evt.Subscribe(async _ =>
        {
            await startWorkGate.Task;
            var backgroundWork = customKind.StartBackgroundWork(async () =>
            {
                await Task.Yield();
                await evt.DisposeAsync();
            });
            disposeFromStartedWork.TrySetResult(backgroundWork);
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
    public async Task StartWithoutCallerContext_WorkLosesTheCallersAsyncLocalValuesAtItsFirstAwait()
    {
        var customKind = new ProbeEvent(new EventingOptions());
        string? valueBeforeFirstAwait = null;
        string? valueAfterFirstAwait = "not read";
        s_callerValue.Value = "caller";

        var work = customKind.StartBackgroundWork(async () =>
        {
            valueBeforeFirstAwait = s_callerValue.Value;
            await Task.Yield();
            valueAfterFirstAwait = s_callerValue.Value;
        });
        s_callerValue.Value = null;
        await work.WaitAsync(Eventually.DefaultTimeout);

        //documented: the work starts inline, so its synchronous start still sees the caller's values
        await Assert.That(valueBeforeFirstAwait).IsEqualTo("caller");
        await Assert.That(valueAfterFirstAwait).IsNull();
    }

    [Test]
    public async Task StartWithoutCallerContext_WithTheFlowAlreadySuppressed_StillStartsTheWork()
    {
        //ExecutionContext.SuppressFlow throws when the flow is already suppressed
        var customKind = new ProbeEvent(new EventingOptions());
        var workRan = false;
        Task work;

        using (ExecutionContext.SuppressFlow())
        {
            work = customKind.StartBackgroundWork(() =>
            {
                workRan = true;
                return Task.CompletedTask;
            });
        }

        await Assert.That(workRan).IsTrue();
        await Assert.That(work.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task StartWithoutCallerContext_WithNullWork_Throws()
    {
        var customKind = new ProbeEvent(new EventingOptions());

        await Assert.That(() => customKind.StartBackgroundWork(null!)).Throws<ArgumentNullException>();
    }
}
