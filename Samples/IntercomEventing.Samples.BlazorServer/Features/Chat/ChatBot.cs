namespace IntercomEventing.Samples.BlazorServer.Features.Chat;

/// <summary>
/// Answers chat commands. It is a hosted service, not a component, so it subscribes with the generated native event
/// (<c>room.MessagePosted += handler</c>) when the app starts and unsubscribes with <c>-=</c> when it stops.
/// </summary>
/// <remarks>
/// Its handler runs on whatever thread raised the message; only components need their handlers moved onto a renderer.
/// </remarks>
public sealed class ChatBot : IHostedService
{
    public const string BotName = "IntercomBot";

    private readonly ChatRoom _room;

    public ChatBot(ChatRoom room)
    {
        _room = room;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _room.MessagePosted += OnMessagePostedAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        //a method group creates an equal delegate each time, so -= finds the subscription that += added
        _room.MessagePosted -= OnMessagePostedAsync;
        return Task.CompletedTask;
    }

    private async Task OnMessagePostedAsync(EventCall<ChatMessage> call)
    {
        var message = call.Payload;
        //the bot's own replies raise MessagePosted too; ignoring them prevents an endless conversation with itself
        var isOwnMessage = message.Author == BotName;
        if (isOwnMessage || !message.Text.StartsWith('/'))
        {
            return;
        }

        var reply = message.Text.Trim().ToLowerInvariant() switch
        {
            "/help" => "Commands: /help, /time, /roll. A message containing !fail makes the statistics panel's handler throw.",
            "/time" => $"Server time is {DateTimeOffset.Now:HH:mm:ss}.",
            "/roll" => $"{message.Author} rolled a {Random.Shared.Next(1, 7)}.",
            _ => $"Unknown command {message.Text}. Try /help.",
        };
        //raising from inside a handler is fine: this nested raise reaches every subscriber, including every open tab
        await _room.PostAsync(BotName, reply);
    }
}
