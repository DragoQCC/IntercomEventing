using System.Reflection;
using System.Runtime.CompilerServices;
using IntercomEventing.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace IntercomEventing.Tests.Core;

/// <summary>
/// What every call carries: <see cref="EventMetadata"/> (ids, name, <c>RaisedAt</c> from the event's clock, caller) and a
/// <see cref="EventCall.CancellationToken"/> linked to the raiser's token and to event disposal.
/// </summary>
public class EventMetadataTests
{
    //the dispatcher waits StartNextEventHandlerAfter plus this documented grace before moving a handler to the background
    private static readonly TimeSpan DocumentedTimeoutGrace = TimeSpan.FromMilliseconds(50);

    [Test]
    public async Task EveryCall_CarriesTheEventsIdAndName()
    {
        var evt = new IntercomEvent<int>(new EventingOptions()) { Name = "ReportReady" };
        var metadata = new List<EventMetadata>();
        evt.Subscribe(call => metadata.Add(call.Metadata));

        await evt.RaiseAsync(1);
        await evt.RaiseAsync(2);

        await Assert.That(metadata).Count().IsEqualTo(2);
        await Assert.That(metadata).All(entry => entry.EventId == evt.Id);
        await Assert.That(metadata).All(entry => entry.EventName == "ReportReady");
    }

    [Test]
    public async Task DefaultName_IsTheReadableTypeName()
    {
        var generic = new IntercomEvent<int>(new EventingOptions());
        var nestedGeneric = new IntercomEvent<List<string>>(new EventingOptions());
        var threshold = new ThresholdEvent<int>(threshold: 1, options: new EventingOptions());
        var named = new ClientConnectedEvent(new EventingOptions());

        await Assert.That(generic.Name).IsEqualTo("IntercomEvent<Int32>");
        await Assert.That(nestedGeneric.Name).IsEqualTo("IntercomEvent<List<String>>");
        await Assert.That(threshold.Name).IsEqualTo("ThresholdEvent<Int32>");
        await Assert.That(named.Name).IsEqualTo("ClientConnectedEvent");
    }

    [Test]
    public async Task EachRaise_GetsItsOwnVersion7CallIdThatSortsByRaiseTime()
    {
        //A10: raising the same call object twice reused its EventCallId in 1.2.0; every raise now builds a new call
        var clock = new FakeTimeProvider();
        var evt = new IntercomEvent<int>(new EventingOptions { TimeProvider = clock });
        var callIds = new List<Guid>();
        evt.Subscribe(call => callIds.Add(call.Metadata.EventCallId));

        for (var raiseNumber = 0; raiseNumber < 5; raiseNumber++)
        {
            await evt.RaiseAsync(raiseNumber);
            clock.Advance(TimeSpan.FromMilliseconds(10));
        }

        await Assert.That(callIds.Distinct().Count()).IsEqualTo(5);
        await Assert.That(callIds).All(callId => callId.Version == 7);
        await Assert.That(callIds).IsInOrder();
    }

    [Test]
    public async Task RaisedAt_IsTheRaiseTimeFromTheEventsTimeProvider()
    {
        //A10: 1.2.0 stamped LastEventTime when the call object was constructed, not when the event was raised
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 9, 30, 0, TimeSpan.Zero));
        var evt = new IntercomEvent<int>(new EventingOptions { TimeProvider = clock });
        var raisedAt = new List<DateTimeOffset>();
        evt.Subscribe(call => raisedAt.Add(call.Metadata.RaisedAt));

        clock.Advance(TimeSpan.FromHours(1));
        var firstRaiseTime = clock.GetUtcNow();
        await evt.RaiseAsync(1);
        clock.Advance(TimeSpan.FromSeconds(5));
        var secondRaiseTime = clock.GetUtcNow();
        await evt.RaiseAsync(2);

        await Assert.That(raisedAt).IsEquivalentTo([firstRaiseTime, secondRaiseTime], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ClockBeforeTheUnixEpoch_StillRaisesAndStampsItsTime()
    {
        //CORE-1: EventCallId came from Guid.CreateVersion7(RaisedAt), which rejects times before 1970-01-01, so with such a
        //clock every raise that had subscribers faulted. EventingOptions.TimeProvider accepts any clock, for example a
        //FakeTimeProvider started at an arbitrary date
        var clock = new FakeTimeProvider(new DateTimeOffset(1969, 12, 31, 23, 59, 0, TimeSpan.Zero));
        var evt = new IntercomEvent<int>(new EventingOptions { TimeProvider = clock });
        var raisedAt = new List<DateTimeOffset>();
        var callIds = new List<Guid>();
        evt.Subscribe(call =>
        {
            raisedAt.Add(call.Metadata.RaisedAt);
            callIds.Add(call.Metadata.EventCallId);
        });

        var systemTimeBeforeRaising = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await evt.RaiseAsync(1);
        await evt.RaiseAsync(2);
        var systemTimeAfterRaising = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await Assert.That(raisedAt).IsEquivalentTo([clock.GetUtcNow(), clock.GetUtcNow()]);
        //a version 7 GUID cannot hold the clock's time, so the ids are stamped with the system time and are still unique
        await Assert.That(callIds).All(callId => callId.Version == 7);
        await Assert.That(callIds.Distinct().Count()).IsEqualTo(2);
        foreach (var callId in callIds)
        {
            var stampedTime = ReadUnixMilliseconds(callId);
            await Assert.That(stampedTime).IsGreaterThanOrEqualTo(systemTimeBeforeRaising);
            await Assert.That(stampedTime).IsLessThanOrEqualTo(systemTimeAfterRaising);
        }
    }

    [Test]
    public async Task ClockExactlyAtTheUnixEpoch_StampsTheCallIdWithItsTime()
    {
        //the boundary of CORE-1: 1970-01-01 itself is the earliest time a version 7 GUID can hold
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var evt = new IntercomEvent<int>(new EventingOptions { TimeProvider = clock });
        var callIds = new List<Guid>();
        evt.Subscribe(call => callIds.Add(call.Metadata.EventCallId));

        await evt.RaiseAsync(1);

        await Assert.That(callIds.Single().Version).IsEqualTo(7);
        await Assert.That(ReadUnixMilliseconds(callIds.Single())).IsEqualTo(0L);
    }

    [Test]
    public async Task Caller_IsRecordedAsPassedAndNullWhenOmitted()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var callers = new List<object?>();
        evt.Subscribe(call => callers.Add(call.Metadata.Caller));
        var raiser = new object();

        await evt.RaiseAsync(1, caller: raiser);
        await evt.RaiseAsync(2);

        await Assert.That(callers).Count().IsEqualTo(2);
        await Assert.That(callers[0]).IsSameReferenceAs(raiser);
        await Assert.That(callers[1]).IsNull();
    }

    [Test]
    public async Task AllHandlersOfOneRaise_ShareOneCall()
    {
        //A10: in 1.2.0 a sequential handler could overwrite EventCaller and the next handler saw the changed value. Every
        //handler of a raise receives the same call; that it cannot be changed is checked by the next test and, as compile
        //errors, by MisuseCompileErrorTests
        var evt = new IntercomEvent<string>(new EventingOptions { DispatchMode = DispatchMode.Sequential });
        var receivedCalls = new List<EventCall<string>>();
        evt.Subscribe(call => receivedCalls.Add(call));
        evt.Subscribe(call => receivedCalls.Add(call));

        await evt.RaiseAsync("payload", caller: "original raiser");

        await Assert.That(receivedCalls).Count().IsEqualTo(2);
        await Assert.That(receivedCalls[1]).IsSameReferenceAs(receivedCalls[0]);
        await Assert.That(receivedCalls[1].Metadata.Caller).IsEqualTo("original raiser");
    }

    [Test]
    public async Task CallsAndMetadata_HaveNoPropertyAHandlerCouldChange()
    {
        //A10: a call is shared by every handler of its raise, so no public property of any call type or of EventMetadata may
        //have a setter that works after construction. Init-only setters are fine: they only build a new object (`with`)
        var checkedTypes = typeof(EventCall).Assembly.GetExportedTypes()
            .Where(IsACallType)
            .Append(typeof(EventMetadata))
            .ToArray();
        var changeableProperties = checkedTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Where(CanBeChangedAfterConstruction)
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .Distinct()
            .ToArray();

        await Assert.That(checkedTypes).Contains(typeof(EventCall<>));
        await Assert.That(checkedTypes).Contains(typeof(ThresholdEventCall<,>));
        await Assert.That(checkedTypes).Contains(typeof(EventMetadata));
        await Assert.That(changeableProperties).IsEmpty();
    }

    [Test]
    public async Task PayloadWithItsOwnMetadataMember_DoesNotHideTheCallMetadata()
    {
        //A11: AvionRelay's call record declared its own Metadata and hid EventCall.Metadata (CS0108); user data now lives in Payload
        var evt = new ClientConnectedEvent(new EventingOptions());
        EventCall<ClientConnected>? receivedCall = null;
        evt.Subscribe(call =>
        {
            receivedCall = call;
        });
        var clientMetadata = new Dictionary<string, string> { ["region"] = "eu-west" };

        await evt.RaiseAsync(new ClientConnected("dashboard", clientMetadata));

        await Assert.That(receivedCall).IsNotNull();
        await Assert.That(receivedCall!.Metadata.EventId).IsEqualTo(evt.Id);
        await Assert.That(receivedCall.Payload.Metadata["region"]).IsEqualTo("eu-west");
    }

    [Test]
    public async Task CallToken_IsCancelledWhenTheRaiserCancels()
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
        using var raiseCancellation = new CancellationTokenSource();

        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);
        await handlerWaiting.Task.WaitAsync(Eventually.DefaultTimeout);
        await raiseCancellation.CancelAsync();
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(Volatile.Read(ref handlerSawCancellation)).IsTrue();
        await Assert.That(evt.IsDisposed).IsFalse();
    }

    [Test]
    public async Task CallToken_WithARaiserToken_IsAlsoCancelledByDisposal()
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
        using var raiseCancellation = new CancellationTokenSource();

        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);
        await handlerWaiting.Task.WaitAsync(Eventually.DefaultTimeout);
        await evt.DisposeAsync();
        await raising.WaitAsync(Eventually.DefaultTimeout);

        await Assert.That(Volatile.Read(ref handlerSawCancellation)).IsTrue();
        await Assert.That(raiseCancellation.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task CallToken_OfAHandlerMovedToTheBackground_IsStillCancelledWhenTheRaiserCancelsLater()
    {
        //the raise's linked token source must outlive RaiseAsync while a handler of that raise runs in the background;
        //disposing it when the raise returns would cut the handler off from the raiser's cancellation
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(10);
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = handlerTimeout, TimeProvider = clock });
        var handlerWaiting = Gate.Create();
        var handlerSawCancellation = Gate.Create();
        evt.Subscribe(call => WaitForCancellationAsync(call, handlerWaiting, handlerSawCancellation));
        using var raiseCancellation = new CancellationTokenSource();

        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);
        await handlerWaiting.Task.WaitAsync(Eventually.DefaultTimeout);
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);
        var handlersInTheBackground = evt.BackgroundWorkCount;
        await raiseCancellation.CancelAsync();
        var handlerWasCancelled = await Eventually.CompletesAsync(handlerSawCancellation.Task);
        await evt.DisposeAsync();

        await Assert.That(handlersInTheBackground).IsEqualTo(1);
        await Assert.That(handlerWasCancelled).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_CancelsAndWaitsForABackgroundedHandlerOfARaiseThatHadARaiserToken()
    {
        //disposal cancels the token every call is linked to; a linked source released when the raise returned would leave the
        //backgrounded handler waiting forever, and DisposeAsync waiting for it
        var clock = new FakeTimeProvider();
        var handlerTimeout = TimeSpan.FromMilliseconds(10);
        var evt = new IntercomEvent<int>(new EventingOptions { StartNextEventHandlerAfter = handlerTimeout, TimeProvider = clock });
        var handlerWaiting = Gate.Create();
        var handlerSawCancellation = Gate.Create();
        evt.Subscribe(call => WaitForCancellationAsync(call, handlerWaiting, handlerSawCancellation));
        using var raiseCancellation = new CancellationTokenSource();

        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);
        await handlerWaiting.Task.WaitAsync(Eventually.DefaultTimeout);
        clock.Advance(handlerTimeout + DocumentedTimeoutGrace);
        await raising.WaitAsync(Eventually.DefaultTimeout);
        var handlersInTheBackground = evt.BackgroundWorkCount;
        var disposed = await Eventually.CompletesAsync(evt.DisposeAsync().AsTask());

        await Assert.That(handlersInTheBackground).IsEqualTo(1);
        await Assert.That(disposed).IsTrue();
        await Assert.That(handlerSawCancellation.Task.IsCompleted).IsTrue();
        await Assert.That(raiseCancellation.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task AlreadyCancelledRaiserToken_ReturnsACancelledTaskAndStartsNoHandler()
    {
        var evt = new IntercomEvent<int>(new EventingOptions());
        var calls = 0;
        evt.Subscribe(_ =>
        {
            Interlocked.Increment(ref calls);
        });
        using var raiseCancellation = new CancellationTokenSource();
        await raiseCancellation.CancelAsync();

        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);

        await Assert.That(raising.IsCanceled).IsTrue();
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(0);
    }

    [Test]
    public async Task RaiserCancellingDuringASequentialRaise_StopsStartingFurtherHandlers()
    {
        var evt = new IntercomEvent<int>(new EventingOptions { DispatchMode = DispatchMode.Sequential });
        using var raiseCancellation = new CancellationTokenSource();
        var secondHandlerRan = false;
        evt.Subscribe(_ => raiseCancellation.Cancel());
        evt.Subscribe(_ =>
        {
            secondHandlerRan = true;
        });

        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);

        await Assert.That(async () => await raising).Throws<OperationCanceledException>();
        await Assert.That(secondHandlerRan).IsFalse();
    }

    [Test]
    public async Task RaiserCancellingWhileHandlersWaitForASlot_StopsStartingThem()
    {
        var evt = new IntercomEvent<int>(new EventingOptions { DispatchMode = DispatchMode.Parallel, MaxConcurrentHandlers = 1 });
        using var raiseCancellation = new CancellationTokenSource();
        var laterHandlerCalls = 0;
        evt.Subscribe(_ => raiseCancellation.Cancel());
        evt.Subscribe(_ =>
        {
            Interlocked.Increment(ref laterHandlerCalls);
        });
        evt.Subscribe(_ =>
        {
            Interlocked.Increment(ref laterHandlerCalls);
        });

        var raising = evt.RaiseAsync(1, cancellationToken: raiseCancellation.Token);

        await Assert.That(async () => await raising.WaitAsync(Eventually.DefaultTimeout)).Throws<OperationCanceledException>();
        await Assert.That(Volatile.Read(ref laterHandlerCalls)).IsEqualTo(0);
    }

    [Test]
    public async Task Deconstruct_SplitsPayloadAndMetadata()
    {
        var evt = new IntercomEvent<UserCreated>(new EventingOptions());
        UserCreated? receivedUser = null;
        EventMetadata? receivedMetadata = null;
        evt.Subscribe(call =>
        {
            var (user, metadata) = call;
            receivedUser = user;
            receivedMetadata = metadata;
        });
        var ada = new UserCreated("Ada", "ada@example.com");

        await evt.RaiseAsync(ada);

        await Assert.That(receivedUser).IsEqualTo(ada);
        await Assert.That(receivedMetadata!.EventId).IsEqualTo(evt.Id);
    }

    [Test]
    public async Task CallToString_NamesTheEventTheCallAndThePayload()
    {
        var evt = new IntercomEvent<string>(new EventingOptions()) { Name = "ReportReady" };
        string? description = null;
        Guid callId = default;
        evt.Subscribe(call =>
        {
            description = call.ToString();
            callId = call.Metadata.EventCallId;
        });

        await evt.RaiseAsync("Q3 report");

        await Assert.That(description).IsEqualTo($"ReportReady call {callId}: Q3 report");
    }

    [Test]
    public async Task CallBuiltForAUnitTest_RequiresMetadata()
    {
        await Assert.That(() => new EventCall<int>(1, null!)).Throws<ArgumentNullException>();
    }

    //signals that it waits, then waits on the call's token until it is cancelled; never fails, so nothing is routed
    private static async Task WaitForCancellationAsync(EventCall<int> call, TaskCompletionSource handlerWaiting,
        TaskCompletionSource handlerSawCancellation)
    {
        handlerWaiting.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, call.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            handlerSawCancellation.TrySetResult();
        }
    }

    //the first 48 bits of a version 7 GUID are its Unix time in milliseconds; the "N" format writes them as the first 12 hex digits
    private static long ReadUnixMilliseconds(Guid version7Id)
    {
        var timestampDigits = version7Id.ToString("N")[..12];
        return Convert.ToInt64(timestampDigits, 16);
    }

    //EventCall itself and every type derived from it, generic definitions included
    private static bool IsACallType(Type type)
    {
        return type == typeof(EventCall) || type.IsSubclassOf(typeof(EventCall));
    }

    //a public setter that is not init-only lets any handler change the call that the other handlers of the raise receive
    private static bool CanBeChangedAfterConstruction(PropertyInfo property)
    {
        var setter = property.SetMethod;
        if (setter is null || !setter.IsPublic)
        {
            return false;
        }
        //the compiler marks an init accessor with a required IsExternalInit modifier on its return value
        var isInitOnly = setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
        return !isInitOnly;
    }
}
