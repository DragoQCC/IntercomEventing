using System.Collections.Concurrent;

namespace IntercomEventing.Benchmark.Examples.GeneratedEvents;

/// <summary>
/// A subscriber that uses the generated members exactly like native .NET events.
/// </summary>
public sealed class ChatTranscript
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public Task OnUserJoinedAsync(EventCall<UserJoined> call)
    {
        Write($"* {call.Payload.Name} joined");
        return Task.CompletedTask;
    }

    public void OnMessagePosted(EventCall<MessagePosted> call)
    {
        Write($"<{call.Payload.Author}> {call.Payload.Text}");
    }

    //takes the base EventCall, so it fits any event; here the payload-less Closed event
    public Task OnRoomClosedAsync(EventCall call)
    {
        Write($"* room closed at {call.Metadata.RaisedAt:HH:mm:ss}");
        return Task.CompletedTask;
    }

    private void Write(string line)
    {
        _lines.Enqueue(line);
        Console.WriteLine(line);
    }
}
