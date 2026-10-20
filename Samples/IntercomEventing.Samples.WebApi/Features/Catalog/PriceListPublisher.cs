namespace IntercomEventing.Samples.WebApi.Features.Catalog;

public sealed record PublishedPriceList(int Version, DateTimeOffset PublishedAt, int ChangesIncluded, IReadOnlyList<Product> Products);

/// <summary>
/// Republishes the price list after a burst of price changes, and raises <see cref="PriceListPublished"/> for each new
/// version. Being both a subscriber and a producer, it chains one event to the next.
/// </summary>
/// <remarks>
/// A hosted service, so it subscribes when the app starts and unsubscribes when it stops. It is also registered as a
/// singleton so endpoints and other services can read <see cref="Current"/> and subscribe to its event.
/// </remarks>
public sealed class PriceListPublisher : IHostedService, IAsyncDisposable
{
    //stands in for rendering and uploading the price list
    private static readonly TimeSpan SimulatedPublishTime = TimeSpan.FromMilliseconds(300);

    private readonly CatalogService _catalog;
    private readonly ILogger<PriceListPublisher> _logger;
    private readonly IntercomEvent<PublishedPriceList> _priceListPublished = new() { Name = "PriceListPublished" };
    private EventSubscription<DebounceEventCall<PriceChange>>? _priceChangeSubscription;
    private volatile PublishedPriceList _current;
    private int _lastVersion = 1;

    public IIntercomEvent<EventCall<PublishedPriceList>> PriceListPublished => _priceListPublished;

    /// <summary>
    /// The latest published price list; it lags behind <see cref="CatalogService.Products"/> by the debounce quiet period.
    /// </summary>
    public PublishedPriceList Current => _current;

    public PriceListPublisher(CatalogService catalog, ILogger<PriceListPublisher> logger)
    {
        _catalog = catalog;
        _logger = logger;
        _current = new PublishedPriceList(_lastVersion, DateTimeOffset.UtcNow, ChangesIncluded: 0, catalog.Products);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _priceChangeSubscription = _catalog.PricesChanged.Subscribe(OnPricesChangedAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_priceChangeSubscription is not null)
        {
            await _priceChangeSubscription.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        return _priceListPublished.DisposeAsync();
    }

    private async Task OnPricesChangedAsync(DebounceEventCall<PriceChange> call)
    {
        await Task.Delay(SimulatedPublishTime, call.CancellationToken);
        //a burst that starts while this one is publishing raises again, so two publishes can overlap; each takes its own version
        var version = Interlocked.Increment(ref _lastVersion);
        var priceList = new PublishedPriceList(version, DateTimeOffset.UtcNow, call.CoalescedTriggerCount, _catalog.Products);
        _current = priceList;

        var lastChange = call.Payload;
        _logger.LogInformation("Published price list v{Version} after {ChangeCount} price change(s); the last was {Sku} {OldPrice} -> {NewPrice}",
            version, call.CoalescedTriggerCount, lastChange.Sku, lastChange.OldPrice, lastChange.NewPrice);
        await _priceListPublished.RaiseAsync(priceList, caller: this);
    }
}
