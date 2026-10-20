namespace IntercomEventing.Benchmark.Examples.GeneratedEvents;

/// <summary>
/// <c>[IntercomEvent]</c>: generated native events on the producer (<c>room.UserJoined += handler</c>) plus a view property
/// for handles and hooks (<c>room.MessagePostedEvent.Subscribe(...)</c>).
/// </summary>
internal sealed class GeneratedEventsExample : IExample
{
    public string Name => "GeneratedEvents";

    public string Summary => "[IntercomEvent]: generated producer.Event += handler members";

    public async Task RunAsync()
    {
        await using var room = new ChatRoom();
        var transcript = new ChatTranscript();

        ExampleCheck.Step("Subscribing through the generated event members and view property");
        room.UserJoined += transcript.OnUserJoinedAsync;
        room.Closed += transcript.OnRoomClosedAsync;
        //the view property gives a disposable handle; Subscribe accepts a synchronous handler too
        var messageSubscription = room.MessagePostedEvent.Subscribe(transcript.OnMessagePosted);
        ExampleCheck.That(room.UserJoinedEvent.SubscriberCount == 1, "UserJoined += reached the private field's event");

        await room.JoinAsync("Ada");
        await room.PostAsync("Ada", "Hello, Grace!");

        ExampleCheck.Step("Unsubscribing with the generated -= and disposing the handle");
        room.UserJoined -= transcript.OnUserJoinedAsync;
        messageSubscription.Dispose();
        await room.JoinAsync("Grace");
        await room.PostAsync("Grace", "Nobody is listening");
        await room.CloseAsync();

        string[] expectedTranscript =
        [
            "* Ada joined",
            "<Ada> Hello, Grace!",
        ];
        var transcriptLines = transcript.Lines.ToArray();
        ExampleCheck.That(transcriptLines.Take(2).SequenceEqual(expectedTranscript), "joins and messages arrived while subscribed");
        ExampleCheck.That(transcriptLines.Length == 3, "nothing arrived after -= and Dispose(), except the Closed event");
        ExampleCheck.That(transcriptLines[2].StartsWith("* room closed", StringComparison.Ordinal), "the base EventCall handler received the payload-less Closed event");
        ExampleCheck.That(room.UserJoinedEvent.SubscriberCount == 0, "UserJoined has no subscribers left");
    }
}
