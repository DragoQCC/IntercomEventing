using IntercomEventing.Samples.BlazorServer.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace IntercomEventing.Samples.BlazorServer.Features.Chat;

/// <summary>
/// A plain component: its subscriptions come from the <c>&lt;EventListener&gt;</c> elements in its markup, which use the
/// subscribe-only views the source generator added to <see cref="ChatRoom"/>.
/// </summary>
public partial class ChatPage
{
    private ErrorBoundary? _statisticsBoundary;
    private DateTimeOffset _newestMessageShown;

    [Inject]
    private ChatRoom Room { get; set; } = default!;

    [Inject]
    private CircuitVisitor Visitor { get; set; } = default!;

    protected string Draft { get; set; } = string.Empty;

    protected string? LastActivity { get; private set; }

    protected async Task SendAsync()
    {
        var text = Draft.Trim();
        if (text.Length == 0)
        {
            return;
        }
        Draft = string.Empty;
        await Room.PostAsync(Visitor.Name, text);
    }

    protected Task ClearAsync()
    {
        return Room.ClearAsync();
    }

    protected void RecoverStatistics()
    {
        //renders ChatStatistics again; the new instance subscribes when it initializes
        _statisticsBoundary?.Recover();
    }

    private void OnMessagePosted(EventCall<ChatMessage> call)
    {
        //the bot answers from inside its handler, and handlers start in subscription order (the bot subscribed first), so its
        //reply can reach this tab before the message it answers. Calls do not arrive in raise order: compare times instead
        var isOlderThanShown = call.Payload.SentAt < _newestMessageShown;
        if (isOlderThanShown)
        {
            return;
        }
        _newestMessageShown = call.Payload.SentAt;
        //the room adds the message to RecentMessages before raising, so the render after this callback already lists it
        LastActivity = $"Last message from {call.Payload.Author} at {call.Metadata.RaisedAt.ToLocalTime():HH:mm:ss}";
    }

    private void OnCleared(EventCall<NoPayload> call)
    {
        LastActivity = $"History cleared at {call.Metadata.RaisedAt.ToLocalTime():HH:mm:ss}";
    }
}
