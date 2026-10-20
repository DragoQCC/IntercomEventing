using System.Collections.Concurrent;

namespace IntercomEventing.Benchmark.Examples.StateChange;

/// <summary>
/// Watches several servers and records every power transition.
/// </summary>
public sealed class PowerAuditLog : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly List<IEventSubscription> _subscriptions = [];

    public IReadOnlyCollection<string> Entries => _entries;

    public void Watch(Server server)
    {
        var subscription = server.PowerChanged.Subscribe(call =>
        {
            var entry = $"{server.Name}: {call.OldState} -> {call.NewState} (requested by {call.Payload.RequestedBy})";
            _entries.Enqueue(entry);
            Console.WriteLine(entry);
        });
        _subscriptions.Add(subscription);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.DisposeAsync();
        }
        _subscriptions.Clear();
    }
}
