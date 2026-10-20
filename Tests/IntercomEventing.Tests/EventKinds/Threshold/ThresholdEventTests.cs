using System.Collections.Concurrent;
using IntercomEventing.Tests.TestSupport;
using TUnit.Assertions.Enums;

namespace IntercomEventing.Tests.EventKinds.Threshold;

/// <summary>
/// <see cref="ThresholdEvent{TValue,TPayload}"/>: atomic increments, <see cref="ThresholdRaiseMode"/>, <c>Reset</c>, custom
/// checks, and calls that carry the values of the increment that raised.
/// </summary>
public class ThresholdEventTests
{
    [Test]
    public async Task EveryTimeReached_RaisesOnEveryIncrementAtOrPastTheThreshold()
    {
        //S8: the 1.x behaviour stays the default (decision D14): threshold 3 and six +1 increments raise at 3, 4, 5 and 6
        var threshold = new ThresholdEvent<int>(threshold: 3, options: new EventingOptions());
        var raisedAt = new List<int>();
        threshold.Subscribe(call => raisedAt.Add(call.CurrentValue));
        var results = new List<bool>();

        for (var increment = 0; increment < 6; increment++)
        {
            results.Add(await threshold.IncrementAsync(1));
        }

        await Assert.That(threshold.RaiseMode).IsEqualTo(ThresholdRaiseMode.EveryTimeReached);
        await Assert.That(raisedAt).IsEquivalentTo([3, 4, 5, 6], CollectionOrdering.Matching);
        await Assert.That(results).IsEquivalentTo([false, false, true, true, true, true], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OnlyWhenCrossed_RaisesOnceAndAgainOnlyAfterDroppingBelow()
    {
        //S8: in 1.2.0 a threshold re-raised forever once passed; OnlyWhenCrossed latches until the value drops below again
        var lockout = new ThresholdEvent<int>(threshold: 3, ThresholdRaiseMode.OnlyWhenCrossed, options: new EventingOptions());
        var crossings = new List<(int Previous, int Current)>();
        lockout.Subscribe(call => crossings.Add((call.PreviousValue, call.CurrentValue)));

        for (var increment = 0; increment < 5; increment++)
        {
            await lockout.IncrementAsync(1);
        }
        await lockout.IncrementAsync(-3);
        await lockout.IncrementAsync(1);

        await Assert.That(crossings).IsEquivalentTo([(2, 3), (2, 3)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reset_LetsTheThresholdBeCrossedAgain()
    {
        //S8: 1.2.0 had no public Reset, so a passed threshold could never be re-armed
        var lockout = new ThresholdEvent<int>(threshold: 3, ThresholdRaiseMode.OnlyWhenCrossed, options: new EventingOptions());
        var crossings = new List<(int Previous, int Current)>();
        lockout.Subscribe(call => crossings.Add((call.PreviousValue, call.CurrentValue)));
        var results = new List<bool>();

        for (var increment = 0; increment < 5; increment++)
        {
            results.Add(await lockout.IncrementAsync(1));
        }
        lockout.Reset();
        var valueAfterReset = lockout.CurrentValue;
        for (var increment = 0; increment < 3; increment++)
        {
            results.Add(await lockout.IncrementAsync(1));
        }
        lockout.Reset(startingValue: 10);
        results.Add(await lockout.IncrementAsync(1));

        await Assert.That(valueAfterReset).IsEqualTo(0);
        await Assert.That(crossings).IsEquivalentTo([(2, 3), (2, 3)], CollectionOrdering.Matching);
        await Assert.That(results.Count(raised => raised)).IsEqualTo(2);
        await Assert.That(lockout.CurrentValue).IsEqualTo(11);
    }

    [Test]
    public async Task Reset_NeverRaises()
    {
        var threshold = new ThresholdEvent<int>(threshold: 1, options: new EventingOptions());
        var calls = 0;
        threshold.Subscribe(_ =>
        {
            Interlocked.Increment(ref calls);
        });

        threshold.Reset(startingValue: 5);
        threshold.Reset();

        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(0);
        await Assert.That(threshold.CurrentValue).IsEqualTo(0);
    }

    [Test]
    public async Task CustomCheck_DecidesWhenTheThresholdIsReached()
    {
        //a falling value: the battery alert raises once the charge drops to 15 or below
        var lowBattery = new ThresholdEvent<int, string>(
            threshold: 15,
            ThresholdRaiseMode.OnlyWhenCrossed,
            isThresholdReached: (charge, limit) => charge <= limit,
            options: new EventingOptions());
        lowBattery.Reset(startingValue: 20);
        var alerts = new List<int>();
        lowBattery.Subscribe(call => alerts.Add(call.CurrentValue));

        await lowBattery.IncrementAsync(-4, "laptop");
        await lowBattery.IncrementAsync(-2, "laptop");
        await lowBattery.IncrementAsync(-2, "laptop");

        await Assert.That(alerts).IsEquivalentTo([14], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(7)]
    [Arguments(-1)]
    public async Task RaiseMode_ThatIsNotDefined_IsRejectedByTheConstructor(int undefinedRaiseMode)
    {
        //an undefined value used to behave as OnlyWhenCrossed without a word
        var raiseMode = (ThresholdRaiseMode)undefinedRaiseMode;

        var thrown = await Assert.That(() => new ThresholdEvent<int, string>(threshold: 1, raiseMode)).Throws<ArgumentOutOfRangeException>();
        var thrownWithoutPayload = await Assert.That(() => new ThresholdEvent<int>(threshold: 1, raiseMode)).Throws<ArgumentOutOfRangeException>();

        await Assert.That(thrown!.ParamName).IsEqualTo("raiseMode");
        await Assert.That(thrownWithoutPayload!.ParamName).IsEqualTo("raiseMode");
    }

    [Test]
    public async Task ThrowingCustomCheck_LeavesTheValueUnchanged()
    {
        var threshold = new ThresholdEvent<int>(
            threshold: 3,
            isThresholdReached: (_, _) => throw new InvalidOperationException("check failed"),
            options: new EventingOptions());

        await Assert.That(async () => await threshold.IncrementAsync(1)).Throws<InvalidOperationException>();
        await Assert.That(threshold.CurrentValue).IsEqualTo(0);
    }

    [Test]
    public async Task Call_CarriesTheThresholdTheValuesAndTheIncrementsPayload()
    {
        var tooManyFailedLogins = new ThresholdEvent<int, LoginAttempt>(threshold: 3, options: new EventingOptions());
        var calls = new List<ThresholdEventCall<int, LoginAttempt>>();
        tooManyFailedLogins.Subscribe(call => calls.Add(call));
        var monitor = new object();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await tooManyFailedLogins.IncrementAsync(1, new LoginAttempt("ada", $"10.0.0.{attempt}"), caller: monitor);
        }

        await Assert.That(calls).Count().IsEqualTo(2);
        await Assert.That(calls[0].Threshold).IsEqualTo(3);
        await Assert.That(calls[0].PreviousValue).IsEqualTo(2);
        await Assert.That(calls[0].CurrentValue).IsEqualTo(3);
        await Assert.That(calls[0].Payload.IpAddress).IsEqualTo("10.0.0.3");
        await Assert.That(calls[1].CurrentValue).IsEqualTo(4);
        await Assert.That(calls[1].Payload.IpAddress).IsEqualTo("10.0.0.4");
        await Assert.That(calls[1].Metadata.Caller).IsSameReferenceAs(monitor);
    }

    [Test]
    public async Task ConcurrentIncrements_LoseNothing()
    {
        //S8 (harness T1): 1,000,000 concurrent increments lost 91-96% of them in 1.2.0
        var threshold = new ThresholdEvent<int>(threshold: 50_000, options: new EventingOptions());
        var raises = 0;
        threshold.Subscribe(_ =>
        {
            Interlocked.Increment(ref raises);
        });

        await Parallel.ForAsync(0, 100_000, async (_, _) => await threshold.IncrementAsync(1));

        await Assert.That(threshold.CurrentValue).IsEqualTo(100_000);
        await Assert.That(Volatile.Read(ref raises)).IsEqualTo(50_001);
    }

    [Test]
    public async Task ConcurrentIncrements_EachCallCarriesItsOwnBeforeAndAfter()
    {
        //S8: 100,000 raises in 1.2.0 reported only 53,865 distinct CurrentValue values
        var threshold = new ThresholdEvent<int>(threshold: 1, options: new EventingOptions { StartNextEventHandlerAfter = null });
        var valuePairs = new ConcurrentBag<(int Previous, int Current)>();
        threshold.Subscribe(call => valuePairs.Add((call.PreviousValue, call.CurrentValue)));

        var results = await Task.WhenAll(Enumerable.Range(0, 400).Select(_ => Task.Run(() => threshold.IncrementAsync(1))))
            .WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(results).All(raised => raised);
        await Assert.That(valuePairs).All(pair => pair.Current == pair.Previous + 1);
        await Assert.That(valuePairs.Select(pair => pair.Current).Order()).IsEquivalentTo(Enumerable.Range(1, 400), CollectionOrdering.Matching);
        await Assert.That(threshold.CurrentValue).IsEqualTo(400);
    }

    [Test]
    public async Task OnlyWhenCrossed_UnderConcurrentUpAndDownIncrements_RaisesOnlyForRealCrossings()
    {
        var crossing = new ThresholdEvent<int>(threshold: 1, ThresholdRaiseMode.OnlyWhenCrossed, options: new EventingOptions());
        var valuePairs = new ConcurrentBag<(int Previous, int Current)>();
        crossing.Subscribe(call => valuePairs.Add((call.PreviousValue, call.CurrentValue)));

        var results = await Task.WhenAll(Enumerable.Range(0, 2000).Select(index => Task.Run(() => crossing.IncrementAsync(index % 2 == 0 ? 1 : -1))))
            .WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(valuePairs).All(pair => pair.Previous < 1 && pair.Current >= 1);
        await Assert.That(results.Count(raised => raised)).IsEqualTo(valuePairs.Count);
        await Assert.That(crossing.CurrentValue).IsEqualTo(0);
    }

    [Test]
    public async Task DecimalValues_AreSupported()
    {
        var budget = new ThresholdEvent<decimal>(threshold: 100.50m, options: new EventingOptions());
        var reached = new List<decimal>();
        budget.Subscribe(call => reached.Add(call.CurrentValue));

        await budget.IncrementAsync(60.25m);
        await budget.IncrementAsync(40.25m);

        await Assert.That(reached).IsEquivalentTo([100.50m], CollectionOrdering.Matching);
    }

    [Test]
    public async Task IncrementMeetingTheCondition_ReturnsTrueEvenWithoutSubscribers()
    {
        //documented: true means the event was raised to every current subscriber, possibly none
        var threshold = new ThresholdEvent<int>(threshold: 1, options: new EventingOptions());

        var raised = await threshold.IncrementAsync(1);

        await Assert.That(raised).IsTrue();
    }

    [Test]
    public async Task IncrementAfterDisposal_ThrowsAndLeavesTheValue()
    {
        //A6
        var threshold = new ThresholdEvent<int>(threshold: 1, options: new EventingOptions());
        await threshold.DisposeAsync();

        await Assert.That(async () => await threshold.IncrementAsync(1)).Throws<ObjectDisposedException>();
        await Assert.That(threshold.CurrentValue).IsEqualTo(0);
    }

    [Test]
    public async Task IncrementWithACancelledToken_ThrowsAndLeavesTheValue()
    {
        var threshold = new ThresholdEvent<int>(threshold: 1, options: new EventingOptions());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.That(async () => await threshold.IncrementAsync(1, cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(threshold.CurrentValue).IsEqualTo(0);
    }

    [Test]
    public async Task CancellationWhileHandlersStart_LeavesTheIncrementApplied()
    {
        //documented: only a token cancelled before the call is reported before the value changes
        var threshold = new ThresholdEvent<int>(threshold: 1, options: new EventingOptions { DispatchMode = DispatchMode.Sequential });
        using var raiseCancellation = new CancellationTokenSource();
        var secondHandlerRan = false;
        threshold.Subscribe(_ => raiseCancellation.Cancel());
        threshold.Subscribe(_ =>
        {
            secondHandlerRan = true;
        });

        await Assert.That(async () => await threshold.IncrementAsync(1, cancellationToken: raiseCancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(threshold.CurrentValue).IsEqualTo(1);
        await Assert.That(secondHandlerRan).IsFalse();
    }

    [Test]
    public async Task IncrementAsync_CompletesOnlyAfterTheHandlersFinished()
    {
        //A12: the returned task covers the raise
        var threshold = new ThresholdEvent<int>(threshold: 1, options: new EventingOptions { StartNextEventHandlerAfter = null });
        var handlerGate = Gate.Create();
        threshold.Subscribe(async _ => await handlerGate.Task);

        var incrementing = threshold.IncrementAsync(1);
        var completedBeforeTheHandler = incrementing.IsCompleted;
        handlerGate.SetResult();
        var raised = await incrementing.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(completedBeforeTheHandler).IsFalse();
        await Assert.That(raised).IsTrue();
    }
}
