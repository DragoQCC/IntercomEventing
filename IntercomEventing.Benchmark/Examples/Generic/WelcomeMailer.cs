using System.Collections.Concurrent;

namespace IntercomEventing.Benchmark.Examples.Generic;

/// <summary>
/// A subscriber with an asynchronous handler. It never names a call type of its own: the event decides it,
/// so <c>registry.UserCreated.Raised += mailer.OnUserCreatedAsync</c> only compiles for a matching handler.
/// </summary>
public sealed class WelcomeMailer
{
    private readonly ConcurrentQueue<EventCall<UserCreated>> _receivedCalls = new();

    public IReadOnlyCollection<EventCall<UserCreated>> ReceivedCalls => _receivedCalls;

    public async Task OnUserCreatedAsync(EventCall<UserCreated> call)
    {
        //the call is this raise's own immutable record: payload plus metadata
        var (user, metadata) = call;
        //stands in for handing the mail to an outbox; honour the call's token in long-running work
        await Task.Delay(TimeSpan.FromMilliseconds(10), call.CancellationToken);
        _receivedCalls.Enqueue(call);
        Console.WriteLine($"Welcome mail queued for {user.Name} <{user.Email}> ({metadata.EventName} call {metadata.EventCallId})");
    }
}
