namespace IntercomEventing.Benchmark.Examples.GeneratedEvents;

public sealed record UserJoined(string Name);

public sealed record MessagePosted(string Author, string Text);

/// <summary>
/// A producer whose events are private fields marked <c>[IntercomEvent]</c>. The source generator adds a native C# event
/// and a subscribe-only view property for each, so subscribers write <c>room.UserJoined += handler</c> and only the room
/// can raise.
/// </summary>
/// <remarks>
/// Generated members:
/// <list type="bullet">
/// <item><description><c>_userJoinedEvent</c>: <c>event Func&lt;EventCall&lt;UserJoined&gt;, Task&gt; UserJoined</c> and
/// <c>IIntercomEvent&lt;EventCall&lt;UserJoined&gt;&gt; UserJoinedEvent</c> (the leading underscore and the trailing
/// <c>Event</c> are dropped).</description></item>
/// <item><description><c>_messagePosted</c>: <c>MessagePosted</c> and <c>MessagePostedEvent</c>.</description></item>
/// <item><description><c>_closing</c> with <c>Name = "Closed"</c>: <c>Closed</c> and <c>ClosedEvent</c>.</description></item>
/// </list>
/// </remarks>
public sealed partial class ChatRoom : IAsyncDisposable
{
    [IntercomEvent]
    private readonly IntercomEvent<UserJoined> _userJoinedEvent = new();

    [IntercomEvent]
    private readonly IntercomEvent<MessagePosted> _messagePosted = new();

    [IntercomEvent(Name = "Closed")]
    private readonly IntercomEvent _closing = new();

    public Task JoinAsync(string name)
    {
        return _userJoinedEvent.RaiseAsync(new UserJoined(name), caller: this);
    }

    public Task PostAsync(string author, string text)
    {
        return _messagePosted.RaiseAsync(new MessagePosted(author, text), caller: this);
    }

    public Task CloseAsync()
    {
        return _closing.RaiseAsync(caller: this);
    }

    //the generated members expose the fields without a way to dispose them, so the room disposes its own events
    public async ValueTask DisposeAsync()
    {
        await _userJoinedEvent.DisposeAsync();
        await _messagePosted.DisposeAsync();
        await _closing.DisposeAsync();
    }
}
