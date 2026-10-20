using IntercomEventing.Samples.WebApi.EventKinds.Batch;
using IntercomEventing.Samples.WebApi.Features.Catalog;
using IntercomEventing.Samples.WebApi.Features.Orders;
using IntercomEventing.Samples.WebApi.Features.Store;

namespace IntercomEventing.Samples.WebApi.Features.Audit;

public sealed record AuditEntry(string EventName, Guid EventCallId, DateTimeOffset RaisedAt, string Summary);

/// <summary>
/// Records an <see cref="AuditEntry"/> for every business event and passes them on in batches through the custom
/// <see cref="BatchEvent{TItem}"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><see cref="RecordAsync"/> takes the <see cref="EventCall"/> base class. Handlers are contravariant, so
/// that one method attaches with <c>Raised +=</c> to events whose call types differ.</description></item>
/// <item><description>A <see cref="TimedEvent"/> flushes a partial batch every <see cref="FlushInterval"/>, so a quiet period
/// does not leave entries unwritten.</description></item>
/// </list>
/// </remarks>
public sealed class AuditTrail : IHostedService, IAsyncDisposable
{
    private const int EntriesPerBatch = 5;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);

    private readonly OrderService _orders;
    private readonly StoreService _store;
    private readonly PriceListPublisher _priceLists;
    private readonly BatchEvent<AuditEntry> _entriesBatched = new(EntriesPerBatch) { Name = "AuditEntriesBatched" };
    private readonly TimedEvent _flushSchedule = new(FlushInterval) { Name = "AuditFlushSchedule" };
    private EventSubscription<TimedEventCall<NoPayload>>? _flushSubscription;

    public IIntercomEvent<BatchEventCall<AuditEntry>> EntriesBatched => _entriesBatched;

    public int PendingEntryCount => _entriesBatched.PendingCount;

    public AuditTrail(OrderService orders, StoreService store, PriceListPublisher priceLists)
    {
        _orders = orders;
        _store = store;
        _priceLists = priceLists;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _orders.OrderPlaced.Raised += RecordAsync;
        _orders.DailySalesGoalReached.Raised += RecordAsync;
        _store.StatusChanged.Raised += RecordAsync;
        _priceLists.PriceListPublished.Raised += RecordAsync;

        _flushSubscription = _flushSchedule.Subscribe(OnFlushDueAsync);
        _flushSchedule.Start(caller: this);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        //a method group creates an equal delegate each time, so -= removes the subscription += added
        _orders.OrderPlaced.Raised -= RecordAsync;
        _orders.DailySalesGoalReached.Raised -= RecordAsync;
        _store.StatusChanged.Raised -= RecordAsync;
        _priceLists.PriceListPublished.Raised -= RecordAsync;

        _flushSchedule.Stop();
        if (_flushSubscription is not null)
        {
            await _flushSubscription.DisposeAsync();
        }
        //writes what is left. AuditArchive is registered before this service, so it stops after it and still receives this batch
        await _entriesBatched.FlushAsync(caller: this);
    }

    /// <returns><see langword="false"/> when no entry was waiting.</returns>
    public Task<bool> FlushNowAsync()
    {
        return _entriesBatched.FlushAsync(caller: this);
    }

    public async ValueTask DisposeAsync()
    {
        await _flushSchedule.DisposeAsync();
        await _entriesBatched.DisposeAsync();
    }

    private Task RecordAsync(EventCall call)
    {
        //EventCall<TPayload>, and so every call type derived from it, overrides ToString with the event name, call id and payload
        var summary = call.ToString() ?? call.Metadata.EventName;
        var entry = new AuditEntry(call.Metadata.EventName, call.Metadata.EventCallId, call.Metadata.RaisedAt, summary);
        //raises the batch, and waits for its handlers, when this entry fills it; otherwise returns at once
        return _entriesBatched.AddAsync(entry, caller: this);
    }

    private Task OnFlushDueAsync(TimedEventCall<NoPayload> call)
    {
        return _entriesBatched.FlushAsync(caller: this);
    }
}
