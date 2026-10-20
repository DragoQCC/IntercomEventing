using System.CodeDom.Compiler;
using System.Reflection;
using IntercomEventing.Generators.Tests.Fixtures;

namespace IntercomEventing.Generators.Tests;

/// <summary>
/// Exercises the members the real generator added to the fixtures in <c>Fixtures/</c> when this project was compiled
/// (the project references the generator as an analyzer).
/// </summary>
public sealed class GeneratedMemberRuntimeTests
{
    [Test]
    public async Task EventAdd_SubscribesThroughRaised()
    {
        var registry = new UserRegistry();
        var receivedNames = new List<string>();
        Func<EventCall<UserCreated>, Task> handler = call =>
        {
            receivedNames.Add(call.Payload.Name);
            return Task.CompletedTask;
        };

        registry.UserCreated += handler;
        await registry.CreateAsync(new UserCreated("Ada"));

        await Assert.That(registry.UserCreatedEvent.SubscriberCount).IsEqualTo(1);
        await Assert.That(receivedNames).IsEquivalentTo(new[] { "Ada" });
    }

    [Test]
    public async Task EventRemove_UnsubscribesThroughRaised()
    {
        var registry = new UserRegistry();
        var callCount = 0;
        Func<EventCall<UserCreated>, Task> handler = _ =>
        {
            callCount++;
            return Task.CompletedTask;
        };

        registry.UserCreated += handler;
        registry.UserCreated -= handler;
        await registry.CreateAsync(new UserCreated("Ada"));

        await Assert.That(registry.UserCreatedEvent.SubscriberCount).IsEqualTo(0);
        await Assert.That(callCount).IsEqualTo(0);
    }

    [Test]
    public async Task EventRemove_RemovesOnlyTheMostRecentEqualHandler()
    {
        //the same semantics as Raised -= (and as native multicast delegates)
        var registry = new UserRegistry();
        var callCount = 0;
        Func<EventCall<UserCreated>, Task> handler = _ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.CompletedTask;
        };

        registry.UserCreated += handler;
        registry.UserCreated += handler;
        registry.UserCreated -= handler;
        await registry.CreateAsync(new UserCreated("Ada"));

        await Assert.That(registry.UserCreatedEvent.SubscriberCount).IsEqualTo(1);
        await Assert.That(callCount).IsEqualTo(1);
    }

    [Test]
    public async Task ViewProperty_ReturnsTheFieldItself()
    {
        var registry = new UserRegistry();

        await Assert.That(registry.UserCreatedEvent).IsSameReferenceAs(registry.UserCreatedBackingEvent);
    }

    [Test]
    public async Task ViewProperty_SubscribeReturnsAWorkingHandle()
    {
        var registry = new UserRegistry();
        var receivedNames = new List<string>();

        var subscription = registry.UserCreatedEvent.Subscribe(call => receivedNames.Add(call.Payload.Name));
        await registry.CreateAsync(new UserCreated("Ada"));
        subscription.Dispose();
        await registry.CreateAsync(new UserCreated("Grace"));

        await Assert.That(receivedNames).IsEquivalentTo(new[] { "Ada" });
        await Assert.That(registry.UserCreatedEvent.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task NameOverride_GeneratesTheRenamedMembersOnly()
    {
        var registry = new UserRegistry();
        var exceededFor = new List<string>();
        registry.LoginsExceeded += call =>
        {
            exceededFor.Add($"{call.Payload.UserName}:{call.CurrentValue}");
            return Task.CompletedTask;
        };

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await registry.RecordFailedLoginAsync(new LoginAttempt("ada"));
        }

        var registryType = typeof(UserRegistry);
        await Assert.That(exceededFor).IsEquivalentTo(new[] { "ada:3" });
        await Assert.That(registryType.GetProperty("LoginsExceededEvent")).IsNotNull();
        await Assert.That(registryType.GetEvent("FailedLogins")).IsNull();
        await Assert.That(registryType.GetProperty("FailedLoginsEvent")).IsNull();
    }

    [Test]
    public async Task FieldNameEndingInEvent_GeneratesTheNameWithoutTheSuffix()
    {
        var room = new ChatRoom();
        var joinedNames = new List<string>();
        room.UserJoined += call =>
        {
            joinedNames.Add(call.Payload.Name);
            return Task.CompletedTask;
        };

        await room.JoinAsync(new UserJoined("Ada"));

        var roomType = typeof(ChatRoom);
        await Assert.That(joinedNames).IsEquivalentTo(new[] { "Ada" });
        await Assert.That(room.UserJoinedEvent).IsSameReferenceAs(room.UserJoinedBackingEvent);
        await Assert.That(roomType.GetEvent("UserJoinedEvent")).IsNull();
        await Assert.That(roomType.GetProperty("UserJoinedEventEvent")).IsNull();
    }

    [Test]
    public async Task GeneratedMembers_ArePublicAndMarkedAsGenerated()
    {
        var generatedEvent = typeof(UserRegistry).GetEvent(nameof(UserRegistry.UserCreated), BindingFlags.Public | BindingFlags.Instance);
        var generatedProperty = typeof(UserRegistry).GetProperty(nameof(UserRegistry.UserCreatedEvent), BindingFlags.Public | BindingFlags.Instance);

        await Assert.That(generatedEvent).IsNotNull();
        await Assert.That(generatedEvent!.EventHandlerType).IsEqualTo(typeof(Func<EventCall<UserCreated>, Task>));
        await Assert.That(generatedEvent.GetCustomAttribute<GeneratedCodeAttribute>()?.Tool).IsEqualTo("IntercomEventing.Generators");
        await Assert.That(generatedProperty).IsNotNull();
        await Assert.That(generatedProperty!.PropertyType).IsEqualTo(typeof(IIntercomEvent<EventCall<UserCreated>>));
        await Assert.That(generatedProperty.CanWrite).IsFalse();
    }

    [Test]
    [NotInParallel(nameof(StaticClock))]
    public async Task StaticField_GeneratesStaticMembersThatForwardToRaised()
    {
        var tickCount = 0;
        Func<EventCall<NoPayload>, Task> handler = _ =>
        {
            tickCount++;
            return Task.CompletedTask;
        };

        StaticClock.Ticked += handler;
        await StaticClock.TickAsync();
        StaticClock.Ticked -= handler;
        await StaticClock.TickAsync();

        var generatedEvent = typeof(StaticClock).GetEvent(nameof(StaticClock.Ticked), BindingFlags.Public | BindingFlags.Static);
        var addAccessorIsStatic = generatedEvent?.AddMethod?.IsStatic ?? false;
        await Assert.That(tickCount).IsEqualTo(1);
        await Assert.That(addAccessorIsStatic).IsTrue();
        await Assert.That(StaticClock.TickedEvent).IsSameReferenceAs(StaticClock.TickedBackingEvent);
        await Assert.That(StaticClock.TickedEvent.SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task GenericContainingType_UsesItsTypeParameterInTheCallType()
    {
        var box = new Box<string>();
        var receivedValues = new List<string>();
        box.Changed += call =>
        {
            receivedValues.Add(call.Payload);
            return Task.CompletedTask;
        };

        await box.SetAsync("first");

        IIntercomEvent<EventCall<string>> typedView = box.ChangedEvent;
        await Assert.That(receivedValues).IsEquivalentTo(new[] { "first" });
        await Assert.That(typedView.SubscriberCount).IsEqualTo(1);
    }

    [Test]
    public async Task NestedGenericContainingTypes_GetTheMembersOnTheInnerType()
    {
        var inner = new Outer<int>.Inner<string>("idle");
        var transitions = new List<string>();
        inner.StateChanged += call =>
        {
            transitions.Add($"{call.OldState}->{call.NewState}");
            return Task.CompletedTask;
        };

        await inner.ChangeAsync("running");

        IIntercomEvent<StateChangeEventCall<string, NoPayload>> typedView = inner.StateChangedEvent;
        await Assert.That(transitions).IsEquivalentTo(new[] { "idle->running" });
        await Assert.That(typedView.SubscriberCount).IsEqualTo(1);
    }

    [Test]
    public async Task GlobalNamespaceProducer_GetsGeneratedMembers()
    {
        var producer = new GlobalNamespaceProducer();
        var total = 0;
        producer.Counted += call =>
        {
            total += call.Payload;
            return Task.CompletedTask;
        };

        await producer.CountAsync(2);
        await producer.CountAsync(3);

        await Assert.That(total).IsEqualTo(5);
    }

    [Test]
    public async Task StructProducer_SharesTheEventAcrossCopies()
    {
        var sensor = new SensorProducer();
        var copy = sensor;
        var readings = new List<double>();
        copy.ReadingTaken += call =>
        {
            readings.Add(call.Payload);
            return Task.CompletedTask;
        };

        await sensor.TakeReadingAsync(1.5);

        await Assert.That(readings).IsEquivalentTo(new[] { 1.5 });
        await Assert.That(sensor.ReadingTakenEvent).IsSameReferenceAs(copy.ReadingTakenEvent);
    }

    [Test]
    public async Task ReplacingANonReadOnlyField_OrphansEarlierSubscribers()
    {
        //the behaviour INTEV005 warns about, through the generated members
        var producer = new ReplaceableProducer();
        var messages = new List<string>();
        producer.MessageSent += call =>
        {
            messages.Add(call.Payload);
            return Task.CompletedTask;
        };

        producer.ReplaceEvent();
        await producer.SendAsync("lost");

        await Assert.That(messages.Count).IsEqualTo(0);
        await Assert.That(producer.MessageSentEvent.SubscriberCount).IsEqualTo(0);
    }
}
