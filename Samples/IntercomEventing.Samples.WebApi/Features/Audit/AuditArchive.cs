using System.Collections.Concurrent;
using IntercomEventing.Samples.WebApi.EventKinds.Batch;

namespace IntercomEventing.Samples.WebApi.Features.Audit;

public sealed record AuditBatch(DateTimeOffset WrittenAt, BatchRaiseReason Reason, IReadOnlyList<AuditEntry> Entries);

/// <summary>
/// Stores the audit batches. It stands in for a database write, which is where batching pays off: one write per batch
/// instead of one per event.
/// </summary>
public sealed class AuditArchive : IHostedService
{
    private const int MaxStoredBatches = 20;

    private readonly AuditTrail _trail;
    private readonly ILogger<AuditArchive> _logger;
    private readonly ConcurrentQueue<AuditBatch> _batches = new();
    private EventSubscription<BatchEventCall<AuditEntry>>? _subscription;

    public IReadOnlyList<AuditBatch> Batches => _batches.ToArray();

    public AuditArchive(AuditTrail trail, ILogger<AuditArchive> logger)
    {
        _trail = trail;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _trail.EntriesBatched.Subscribe(OnEntriesBatched);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
        }
    }

    private void OnEntriesBatched(BatchEventCall<AuditEntry> call)
    {
        _batches.Enqueue(new AuditBatch(call.Metadata.RaisedAt, call.Reason, call.Items));
        while (_batches.Count > MaxStoredBatches)
        {
            _batches.TryDequeue(out _);
        }
        _logger.LogInformation("Archived {EntryCount} audit entries ({Reason})", call.Items.Count, call.Reason);
    }
}
