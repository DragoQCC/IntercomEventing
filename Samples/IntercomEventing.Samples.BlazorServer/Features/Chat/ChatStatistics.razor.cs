using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Samples.BlazorServer.Features.Chat;

/// <summary>
/// Counts messages per author. Its handler deliberately throws for a message containing <c>!fail</c> to show where a
/// handler exception goes in a component: no exception handler is passed to <c>Subscribe</c>, so the exception reaches the
/// nearest <c>ErrorBoundary</c>, which replaces this component with its error content and disposes it.
/// </summary>
public partial class ChatStatistics
{
    private const string FailureTrigger = "!fail";

    private readonly Dictionary<string, int> _messagesPerAuthor = [];

    [Inject]
    private ChatRoom Room { get; set; } = default!;

    protected IReadOnlyDictionary<string, int> MessagesPerAuthor => _messagesPerAuthor;

    protected override void OnInitialized()
    {
        foreach (var message in Room.RecentMessages)
        {
            CountMessageFrom(message.Author);
        }
        Events.Subscribe(Room.MessagePostedEvent, OnMessagePosted);
        Events.Subscribe(Room.ClearedEvent, _ => _messagesPerAuthor.Clear());
    }

    private void OnMessagePosted(EventCall<ChatMessage> call)
    {
        var message = call.Payload;
        if (message.Text.Contains(FailureTrigger, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{message.Author} sent a message the statistics panel cannot count.");
        }
        CountMessageFrom(message.Author);
    }

    private void CountMessageFrom(string author)
    {
        _messagesPerAuthor[author] = _messagesPerAuthor.GetValueOrDefault(author) + 1;
    }
}
