namespace IntercomEventing.Benchmark.Examples.Generic;

/// <summary>
/// <c>IntercomEvent&lt;TPayload&gt;</c>: a producer raises a payload, each raise creates one immutable call, and subscribers
/// attach with <c>Raised +=</c> or <c>Subscribe</c>. Also shows a subscribe-only view and a payload-less event.
/// </summary>
internal sealed class GenericEventExample : IExample
{
    public string Name => "Generic";

    public string Summary => "IntercomEvent<TPayload>: raise a payload, receive one EventCall per raise";

    public async Task RunAsync()
    {
        await using var registry = new UserRegistry();
        var mailer = new WelcomeMailer();
        var removedNames = new List<string>();
        var clearedAt = new List<DateTimeOffset>();

        ExampleCheck.Step("Subscribing: Raised += on the event, Subscribe on the subscribe-only view and on a payload-less event");
        registry.UserCreated.Raised += mailer.OnUserCreatedAsync;
        using var removedSubscription = registry.UserRemoved.Subscribe(call => removedNames.Add(call.Payload.Name));
        using var clearedSubscription = registry.Cleared.Subscribe(call => clearedAt.Add(call.Metadata.RaisedAt));

        ExampleCheck.Step("Raising: two users created, one removed, then the registry is cleared");
        await registry.CreateAsync("Ada", "ada@example.com");
        await registry.CreateAsync("Grace", "grace@example.com");
        await registry.RemoveAsync("Ada");
        await registry.ClearAsync();

        var calls = mailer.ReceivedCalls.ToArray();
        ExampleCheck.That(calls.Length == 2, "RaiseAsync completed after the handler ran, once per raise");
        var payloadsMatchTheirRaises = calls[0].Payload.Name == "Ada" && calls[1].Payload.Name == "Grace";
        ExampleCheck.That(payloadsMatchTheirRaises, "each call carries the payload of its own raise");
        var eachRaiseHasItsOwnCallId = calls[0].Metadata.EventCallId != calls[1].Metadata.EventCallId;
        ExampleCheck.That(eachRaiseHasItsOwnCallId, "each raise has its own EventCallId");
        var callsIdentifyTheEvent = calls.All(call => call.Metadata.EventId == registry.UserCreated.Id && call.Metadata.EventName == "UserCreated");
        ExampleCheck.That(callsIdentifyTheEvent, "Metadata.EventId and EventName identify the event");
        ExampleCheck.That(ReferenceEquals(calls[0].Metadata.Caller, registry), "Metadata.Caller is the object passed as caller: this");
        ExampleCheck.That(removedNames.SequenceEqual(["Ada"]), "the subscribe-only view delivered UserRemoved");
        ExampleCheck.That(clearedAt.Count == 1, "the payload-less event delivered its call");

        ExampleCheck.Step("Unsubscribing with -= and the same method group");
        registry.UserCreated.Raised -= mailer.OnUserCreatedAsync;
        await registry.CreateAsync("Linus", "linus@example.com");
        ExampleCheck.That(mailer.ReceivedCalls.Count == 2, "after -= the mailer receives no further calls");
        ExampleCheck.That(registry.UserCreated.SubscriberCount == 0, "UserCreated has no subscribers left");
    }
}
