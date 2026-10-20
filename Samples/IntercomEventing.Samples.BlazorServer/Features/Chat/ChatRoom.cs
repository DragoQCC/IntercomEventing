namespace IntercomEventing.Samples.BlazorServer.Features.Chat;

public sealed record ChatMessage(string Author, string Text, DateTimeOffset SentAt);

/// <summary>
/// A chat room shared by every browser tab (registered as a singleton).
/// </summary>
/// <remarks>
/// <para>The events are private fields marked <c>[IntercomEvent]</c>. For each one the source generator adds two public members:</para>
/// <list type="bullet">
/// <item><description><c>MessagePosted</c> / <c>Cleared</c>: native .NET events, so <c>room.MessagePosted += handler</c> works
/// (used by <see cref="ChatBot"/>).</description></item>
/// <item><description><c>MessagePostedEvent</c> / <c>ClearedEvent</c>: the subscribe-only <c>IIntercomEvent</c> views, which
/// components pass to <c>&lt;EventListener&gt;</c> or <c>Events.Subscribe</c>.</description></item>
/// </list>
/// <para>Neither gives subscribers a way to raise or dispose the events; only the room does.</para>
/// </remarks>
public sealed partial class ChatRoom : IAsyncDisposable
{
    private const int MaxRecentMessages = 50;

    private readonly Lock _messagesLock = new();
    private readonly List<ChatMessage> _recentMessages = [];

    [IntercomEvent]
    private readonly IntercomEvent<ChatMessage> _messagePosted = new() { Name = "MessagePosted" };

    //payload-less event; the generated members are named Cleared and ClearedEvent
    [IntercomEvent(Name = "Cleared")]
    private readonly IntercomEvent _historyCleared = new() { Name = "Cleared" };

    /// <summary>
    /// A copy of the latest messages, oldest first. The room updates it before raising, so a handler can read the message
    /// it was raised for here.
    /// </summary>
    public IReadOnlyList<ChatMessage> RecentMessages
    {
        get
        {
            lock (_messagesLock)
            {
                return _recentMessages.ToArray();
            }
        }
    }

    public async Task PostAsync(string author, string text)
    {
        var message = new ChatMessage(author, text, DateTimeOffset.UtcNow);
        lock (_messagesLock)
        {
            _recentMessages.Add(message);
            if (_recentMessages.Count > MaxRecentMessages)
            {
                _recentMessages.RemoveAt(0);
            }
        }
        await _messagePosted.RaiseAsync(message, caller: this);
    }

    public async Task ClearAsync()
    {
        lock (_messagesLock)
        {
            _recentMessages.Clear();
        }
        await _historyCleared.RaiseAsync(caller: this);
    }

    public async ValueTask DisposeAsync()
    {
        await _messagePosted.DisposeAsync();
        await _historyCleared.DisposeAsync();
    }
}
