using System.Collections.Concurrent;

namespace IntercomEventing.Benchmark.Examples.Subscriptions;

/// <summary>
/// One handler for every event: <see cref="EventCall"/> is the base of every call type, and <c>Func&lt;TCall, Task&gt;</c> is
/// contravariant, so the same method group attaches to <c>OrderPlaced</c> and <c>OrderCancelled</c>.
/// </summary>
public sealed class AuditLog
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public Task RecordAsync(EventCall call)
    {
        var entry = $"{call.Metadata.EventName} call {call.Metadata.EventCallId} at {call.Metadata.RaisedAt:HH:mm:ss.fff}";
        _entries.Enqueue(entry);
        Console.WriteLine($"Audit: {entry}");
        return Task.CompletedTask;
    }
}
