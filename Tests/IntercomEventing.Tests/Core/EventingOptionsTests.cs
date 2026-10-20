using IntercomEventing.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// <see cref="EventingOptions"/>: documented defaults, validation when the options are built, and how events read
/// <see cref="EventingOptions.Default"/>.
/// </summary>
public class EventingOptionsTests
{
    //the dispatcher waits StartNextEventHandlerAfter plus this documented grace before moving a handler to the background
    private static readonly TimeSpan DocumentedTimeoutGrace = TimeSpan.FromMilliseconds(50);

    public static IEnumerable<TimeSpan> OutOfRangeHandlerTimeouts()
    {
        //A5: TimeSpan.MaxValue overflowed and 60 days threw ArgumentOutOfRangeException at raise time in 1.2.0
        yield return TimeSpan.MaxValue;
        yield return TimeSpan.FromDays(60);
        yield return TimeSpan.FromDays(49) + TimeSpan.FromTicks(1);
        yield return TimeSpan.FromMilliseconds(-2);
    }

    public static IEnumerable<TimeSpan> AcceptedHandlerTimeouts()
    {
        yield return TimeSpan.Zero;
        yield return TimeSpan.FromMilliseconds(1);
        yield return TimeSpan.FromDays(49);
    }

    [Test]
    public async Task NewOptions_HaveTheDocumentedDefaults()
    {
        var options = new EventingOptions();

        await Assert.That(options.DispatchMode).IsEqualTo(DispatchMode.Parallel);
        //S4: 1.2.0 documented a sequential default of 1 but kept 100; the value is now documented as Parallel-only with default 100
        await Assert.That(options.MaxConcurrentHandlers).IsEqualTo(100);
        await Assert.That(options.StartNextEventHandlerAfter).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(options.AllowMultipleSubscribers).IsTrue();
        await Assert.That(options.DefaultExceptionHandler).IsNull();
        await Assert.That(options.TimeProvider).IsSameReferenceAs(TimeProvider.System);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task MaxConcurrentHandlers_BelowOne_IsRejectedWhenTheOptionsAreBuilt(int maxConcurrentHandlers)
    {
        //A3: in 1.2.0 a value of 0 or -1 made a parallel raise with two subscribers spin forever
        var thrown = await Assert.That(() => new EventingOptions { MaxConcurrentHandlers = maxConcurrentHandlers })
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(thrown!.ParamName).IsEqualTo(nameof(EventingOptions.MaxConcurrentHandlers));
    }

    [Test]
    [MethodDataSource(nameof(OutOfRangeHandlerTimeouts))]
    public async Task StartNextEventHandlerAfter_OutsideZeroTo49Days_IsRejectedWhenTheOptionsAreBuilt(TimeSpan timeout)
    {
        //A5: validated at construction, so an invalid timeout can no longer fail a raise
        var thrown = await Assert.That(() => new EventingOptions { StartNextEventHandlerAfter = timeout })
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(thrown!.ParamName).IsEqualTo(nameof(EventingOptions.StartNextEventHandlerAfter));
    }

    [Test]
    [MethodDataSource(nameof(AcceptedHandlerTimeouts))]
    public async Task StartNextEventHandlerAfter_FromZeroTo49Days_IsKept(TimeSpan timeout)
    {
        var options = new EventingOptions { StartNextEventHandlerAfter = timeout };

        await Assert.That(options.StartNextEventHandlerAfter).IsEqualTo(timeout);
    }

    [Test]
    public async Task StartNextEventHandlerAfter_InfiniteTimeSpan_IsStoredAsWaitForEveryHandler()
    {
        //A5: in 1.2.0 InfiniteTimeSpan (-1 ms) plus the 50 ms grace became a 49 ms timeout
        var options = new EventingOptions { StartNextEventHandlerAfter = Timeout.InfiniteTimeSpan };

        await Assert.That(options.StartNextEventHandlerAfter).IsNull();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task WaitForEveryHandler_KeepsTheRaiseOpenUntilTheHandlerFinishes(bool useInfiniteTimeSpan)
    {
        //A5: both spellings of "no timeout" wait however long the handler takes; the fake clock moving an hour changes nothing
        var clock = new FakeTimeProvider();
        TimeSpan? noTimeout = useInfiniteTimeSpan ? Timeout.InfiniteTimeSpan : null;
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = noTimeout, TimeProvider = clock });
        var handlerGate = Gate.Create();
        evt.Subscribe(async _ => await handlerGate.Task);

        var raising = evt.RaiseAsync(1);
        clock.Advance(TimeSpan.FromHours(1));
        var stillWaitingAfterAnHour = await Eventually.StaysIncompleteAsync(raising);
        handlerGate.SetResult();
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(stillWaitingAfterAnHour).IsTrue();
        await Assert.That(evt.BackgroundWorkCount).IsEqualTo(0);
        await evt.DisposeAsync();
    }

    [Test]
    public async Task ZeroTimeout_MovesASlowHandlerToTheBackgroundAfterTheGrace()
    {
        var clock = new FakeTimeProvider();
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = TimeSpan.Zero, TimeProvider = clock });
        var handlerGate = Gate.Create();
        evt.Subscribe(async _ => await handlerGate.Task);

        var raising = evt.RaiseAsync(1);
        var waitingDuringTheGrace = !raising.IsCompleted;
        clock.Advance(DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(waitingDuringTheGrace).IsTrue();
        await Assert.That(evt.BackgroundWorkCount).IsEqualTo(1);
        handlerGate.SetResult();
        await evt.DisposeAsync();
    }

    [Test]
    [Arguments(42)]
    [Arguments(-1)]
    public async Task DispatchMode_ThatIsNotDefined_IsRejectedWhenTheOptionsAreBuilt(int undefinedMode)
    {
        //an undefined value from a cast or numeric configuration used to dispatch as Parallel without a word
        var thrown = await Assert.That(() => new EventingOptions { DispatchMode = (DispatchMode)undefinedMode })
            .Throws<ArgumentOutOfRangeException>();
        var thrownByWith = await Assert.That(() => EventingOptions.Default with { DispatchMode = (DispatchMode)undefinedMode })
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(thrown!.ParamName).IsEqualTo(nameof(EventingOptions.DispatchMode));
        await Assert.That(thrownByWith!.ParamName).IsEqualTo(nameof(EventingOptions.DispatchMode));
    }

    [Test]
    public async Task TimeProvider_Null_IsRejected()
    {
        await Assert.That(() => new EventingOptions { TimeProvider = null! }).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WithExpression_ChangesOneSettingAndKeepsTheOthers()
    {
        var failures = new FailureRecorder();
        var baseOptions = new EventingOptions
        {
            MaxConcurrentHandlers = 7,
            DefaultExceptionHandler = failures.Record,
        };

        var derivedOptions = baseOptions with { DispatchMode = DispatchMode.Sequential };

        await Assert.That(derivedOptions.DispatchMode).IsEqualTo(DispatchMode.Sequential);
        await Assert.That(derivedOptions.MaxConcurrentHandlers).IsEqualTo(7);
        await Assert.That(derivedOptions.DefaultExceptionHandler).IsSameReferenceAs(baseOptions.DefaultExceptionHandler);
        await Assert.That(baseOptions.DispatchMode).IsEqualTo(DispatchMode.Parallel);
    }

    [Test]
    [NotInParallel]
    public async Task Default_Null_IsRejectedAndTheDefaultIsKept()
    {
        var defaultBefore = EventingOptions.Default;

        await Assert.That(() => EventingOptions.Default = null!).Throws<ArgumentNullException>();
        await Assert.That(EventingOptions.Default).IsSameReferenceAs(defaultBefore);
    }

    [Test]
    [NotInParallel]
    public async Task EventWithoutOwnOptions_ReadsDefaultAtEachRaise()
    {
        //A9: 1.2.0 computed its static configuration once; events created before a change now pick it up at their next raise
        try
        {
            var createdBeforeTheChange = new IntercomEvent<int>();
            Func<EventCall<int>, Task> failingHandler = _ => throw new InvalidOperationException("handler failure");
            createdBeforeTheChange.Subscribe(failingHandler);
            var failures = new FailureRecorder();

            EventingOptions.Default = new EventingOptions { DefaultExceptionHandler = failures.Record };
            await createdBeforeTheChange.RaiseAsync(1);

            await Assert.That(failures.Count).IsEqualTo(1);
            await createdBeforeTheChange.DisposeAsync();
        }
        finally
        {
            IntercomEventingServiceCollectionExtensions.ResetProcessDefaultOptionsForTesting();
        }
    }

    [Test]
    [NotInParallel]
    public async Task EventWithOwnOptions_IgnoresDefault()
    {
        try
        {
            var ownFailures = new FailureRecorder();
            var defaultFailures = new FailureRecorder();
            var evt = new IntercomEvent<int>(new EventingOptions { DefaultExceptionHandler = ownFailures.Record });
            Func<EventCall<int>, Task> failingHandler = _ => throw new InvalidOperationException("handler failure");
            evt.Subscribe(failingHandler);

            EventingOptions.Default = new EventingOptions { DefaultExceptionHandler = defaultFailures.Record };
            await evt.RaiseAsync(1);

            await Assert.That(ownFailures.Count).IsEqualTo(1);
            await Assert.That(defaultFailures.Count).IsEqualTo(0);
            await evt.DisposeAsync();
        }
        finally
        {
            IntercomEventingServiceCollectionExtensions.ResetProcessDefaultOptionsForTesting();
        }
    }

    [Test]
    public async Task Options_OfEveryEventKind_AreTheOptionsItWasCreatedWith_AlsoThroughTheSubscribeOnlyView()
    {
        //code outside the core (the Blazor package) reads an event's options through IIntercomEvent to route failures
        var ownOptions = new EventingOptions { DispatchMode = DispatchMode.Sequential };
        IIntercomEvent<EventCall<int>> genericEvent = new IntercomEvent<int>(ownOptions);
        IIntercomEvent<ThresholdEventCall<int, NoPayload>> thresholdEvent = new ThresholdEvent<int>(threshold: 1, options: ownOptions);
        IIntercomEvent<StateChangeEventCall<int, NoPayload>> stateChangeEvent = new StateChangeEvent<int>(initialState: 0, options: ownOptions);
        IIntercomEvent<TimedEventCall<NoPayload>> timedEvent = new TimedEvent(TimeSpan.FromMinutes(1), options: ownOptions);
        IIntercomEvent<DebounceEventCall<NoPayload>> debounceEvent = new DebounceEvent(TimeSpan.FromMinutes(1), options: ownOptions);
        var customKind = new ProbeEvent(ownOptions);

        await Assert.That(genericEvent.Options).IsSameReferenceAs(ownOptions);
        await Assert.That(thresholdEvent.Options).IsSameReferenceAs(ownOptions);
        await Assert.That(stateChangeEvent.Options).IsSameReferenceAs(ownOptions);
        await Assert.That(timedEvent.Options).IsSameReferenceAs(ownOptions);
        await Assert.That(debounceEvent.Options).IsSameReferenceAs(ownOptions);
        await Assert.That(customKind.Options).IsSameReferenceAs(ownOptions);
    }

    [Test]
    [NotInParallel]
    public async Task Options_WithoutOwnOptions_AreTheDefaultAtTheMomentOfReading()
    {
        try
        {
            IIntercomEvent<EventCall<int>> subscribeOnlyView = new IntercomEvent<int>();
            var defaultBeforeTheChange = EventingOptions.Default;
            var optionsBeforeTheChange = subscribeOnlyView.Options;

            var changedDefault = new EventingOptions { MaxConcurrentHandlers = 3 };
            EventingOptions.Default = changedDefault;
            var optionsAfterTheChange = subscribeOnlyView.Options;

            await Assert.That(optionsBeforeTheChange).IsSameReferenceAs(defaultBeforeTheChange);
            await Assert.That(optionsAfterTheChange).IsSameReferenceAs(changedDefault);
        }
        finally
        {
            IntercomEventingServiceCollectionExtensions.ResetProcessDefaultOptionsForTesting();
        }
    }
}
