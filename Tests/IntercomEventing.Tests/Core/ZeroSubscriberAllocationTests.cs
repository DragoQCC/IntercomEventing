using IntercomEventing.Tests.TestSupport;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// A raise with no subscribers builds no metadata, call or token source, so it allocates nothing. Measured with
/// <see cref="AllocationMeasurement"/> over many synchronous raises after a warm-up; the fewest bytes of any window count.
/// </summary>
/// <remarks>
/// <para>The judges found the synthesis prototype allocated a 232 B closure per raise even without subscribers; the core port
/// fixed it.</para>
/// <para>Raise methods written as <c>async</c> methods (threshold, state change, debounce flush) are allocation-free only when
/// IntercomEventing is compiled with optimizations: an unoptimized (Debug) build gives every async method a class state machine.
/// Those cases are measured in Release builds and skipped in Debug builds.</para>
/// </remarks>
public class ZeroSubscriberAllocationTests
{
    private const int MeasuredRaises = 20_000;

    public enum ZeroSubscriberRaise
    {
        PayloadEvent,
        PayloadEventWithCaller,
        PayloadlessEvent,
        RaiseWithACancellableToken,
        EventWithItsOwnOptions,
        EventWhoseLastSubscriberWasRemoved,
        ThresholdIncrementThatRaises,
        ThresholdIncrementThatDoesNotRaise,
        ThresholdCrossingBackAndForth,
        StateChangeThatChanges,
        StateChangeThatDoesNotChange,
        DebounceFlushWithNothingPending,
        CustomEventKindWithAStaticFactory,
    }

    public static IEnumerable<ZeroSubscriberRaise> EveryRaiseCase()
    {
        return Enum.GetValues<ZeroSubscriberRaise>();
    }

    [Test]
    [MethodDataSource(nameof(EveryRaiseCase))]
    public async Task RaiseWithoutSubscribers_AllocatesNothing(ZeroSubscriberRaise raiseCase)
    {
        var measuredOnlyWhenOptimized = IsRaisedThroughAnAsyncMethod(raiseCase) && !AllocationMeasurement.IsLibraryOptimized();
        Skip.When(measuredOnlyWhenOptimized,
            "IntercomEventing is a Debug build here: unoptimized async methods allocate a class state machine, so this async raise method is measured in Release builds.");
        using var raiseCancellation = new CancellationTokenSource();
        var raise = CreateRaise(raiseCase, raiseCancellation.Token);

        var allocatedBytes = await AllocationMeasurement.MeasureAllocatedBytesAsync(raise, MeasuredRaises);

        await Assert.That(allocatedBytes).IsEqualTo(0L);
    }

    [Test]
    public async Task RaiseWithOneSubscriber_DoesAllocate()
    {
        //control: proves the measurement sees allocations, so the zero results above are meaningful
        var evt = new IntercomEvent<int>(new EventingOptions());
        evt.Subscribe(_ => { });

        var allocatedBytes = await AllocationMeasurement.MeasureAllocatedBytesAsync(() => evt.RaiseAsync(1), 1_000);

        await Assert.That(allocatedBytes).IsGreaterThan(0L);
    }

    //ThresholdEvent.IncrementAsync, StateChangeEvent.ChangeStateAsync and DebounceEvent.FlushAsync are async methods
    private static bool IsRaisedThroughAnAsyncMethod(ZeroSubscriberRaise raiseCase)
    {
        return raiseCase is ZeroSubscriberRaise.ThresholdIncrementThatRaises
            or ZeroSubscriberRaise.ThresholdIncrementThatDoesNotRaise
            or ZeroSubscriberRaise.ThresholdCrossingBackAndForth
            or ZeroSubscriberRaise.StateChangeThatChanges
            or ZeroSubscriberRaise.StateChangeThatDoesNotChange
            or ZeroSubscriberRaise.DebounceFlushWithNothingPending;
    }

    private static Func<Task> CreateRaise(ZeroSubscriberRaise raiseCase, CancellationToken cancellableToken)
    {
        var caller = new object();
        switch (raiseCase)
        {
            case ZeroSubscriberRaise.PayloadEvent:
            {
                var evt = new IntercomEvent<int>(new EventingOptions());
                return () => evt.RaiseAsync(1);
            }
            case ZeroSubscriberRaise.PayloadEventWithCaller:
            {
                var evt = new IntercomEvent<string>(new EventingOptions());
                return () => evt.RaiseAsync("payload", caller);
            }
            case ZeroSubscriberRaise.PayloadlessEvent:
            {
                var evt = new IntercomEvent(new EventingOptions());
                return () => evt.RaiseAsync();
            }
            case ZeroSubscriberRaise.RaiseWithACancellableToken:
            {
                var evt = new IntercomEvent<int>(new EventingOptions());
                return () => evt.RaiseAsync(1, caller, cancellableToken);
            }
            case ZeroSubscriberRaise.EventWithItsOwnOptions:
            {
                var evt = new IntercomEvent<int>(new EventingOptions { DispatchMode = DispatchMode.Sequential });
                return () => evt.RaiseAsync(1);
            }
            case ZeroSubscriberRaise.EventWhoseLastSubscriberWasRemoved:
            {
                var evt = new IntercomEvent<int>(new EventingOptions());
                var subscription = evt.Subscribe(_ => { });
                subscription.Dispose();
                Func<EventCall<int>, Task> raisedHandler = _ => Task.CompletedTask;
                evt.Raised += raisedHandler;
                evt.Raised -= raisedHandler;
                return () => evt.RaiseAsync(1);
            }
            case ZeroSubscriberRaise.ThresholdIncrementThatRaises:
            {
                var threshold = new ThresholdEvent<int, string>(threshold: 0, options: new EventingOptions());
                return () => threshold.IncrementAsync(1, "payload", caller);
            }
            case ZeroSubscriberRaise.ThresholdIncrementThatDoesNotRaise:
            {
                var threshold = new ThresholdEvent<int>(threshold: int.MaxValue, options: new EventingOptions());
                return () => threshold.IncrementAsync(0);
            }
            case ZeroSubscriberRaise.ThresholdCrossingBackAndForth:
            {
                var threshold = new ThresholdEvent<long>(threshold: 1, ThresholdRaiseMode.OnlyWhenCrossed, options: new EventingOptions());
                var increments = 0;
                return () =>
                {
                    increments++;
                    var amount = increments % 2 == 1 ? 1L : -1L;
                    return threshold.IncrementAsync(amount);
                };
            }
            case ZeroSubscriberRaise.StateChangeThatChanges:
            {
                var state = new StateChangeEvent<int, string>(0, options: new EventingOptions());
                var changes = 0;
                return () =>
                {
                    changes++;
                    return state.ChangeStateAsync(changes % 2, "payload", caller);
                };
            }
            case ZeroSubscriberRaise.StateChangeThatDoesNotChange:
            {
                var state = new StateChangeEvent<long>(5, options: new EventingOptions());
                return () => state.ChangeStateAsync(5);
            }
            case ZeroSubscriberRaise.DebounceFlushWithNothingPending:
            {
                var debounce = new DebounceEvent<int>(TimeSpan.FromSeconds(10), new EventingOptions());
                return () => debounce.FlushAsync();
            }
            case ZeroSubscriberRaise.CustomEventKindWithAStaticFactory:
            {
                var probe = new ProbeEvent(new EventingOptions());
                return () => probe.RaiseAsync(1);
            }
            default:
            {
                throw new ArgumentOutOfRangeException(nameof(raiseCase), raiseCase, "Unknown raise case.");
            }
        }
    }
}
