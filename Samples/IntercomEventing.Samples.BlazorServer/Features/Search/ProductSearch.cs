namespace IntercomEventing.Samples.BlazorServer.Features.Search;

public sealed record SearchQuery(string Text);

public sealed record CatalogProduct(string Name, string Category);

/// <summary>
/// The search box of one browser tab. Registered as scoped, so each circuit gets its own instance and its own event, and the
/// circuit's scope disposes it when the tab closes.
/// </summary>
/// <remarks>
/// Every keystroke calls <see cref="DebounceEvent{TPayload}.Trigger"/>. The event raises once typing has paused for
/// <see cref="QuietPeriod"/>, with the last query and the number of keystrokes it absorbed.
/// </remarks>
public sealed class ProductSearch : IAsyncDisposable
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(400);

    //stands in for a remote search service
    private static readonly TimeSpan SimulatedSearchLatency = TimeSpan.FromMilliseconds(150);

    private static readonly CatalogProduct[] Catalog =
    [
        new("Mechanical keyboard", "Peripherals"),
        new("Wireless mouse", "Peripherals"),
        new("27 inch monitor", "Displays"),
        new("Portable monitor", "Displays"),
        new("USB-C dock", "Accessories"),
        new("Laptop stand", "Accessories"),
        new("Noise cancelling headset", "Audio"),
        new("Desk microphone", "Audio"),
        new("Rack mount shelf", "Server room"),
        new("Patch cable pack", "Server room"),
    ];

    public DebounceEvent<SearchQuery> QueryChanged { get; } = new(QuietPeriod) { Name = "QueryChanged" };

    public int KeystrokeCount { get; private set; }

    /// <summary>
    /// Called on every keystroke. Never waits: the raise happens later in the background.
    /// </summary>
    public void Type(string text)
    {
        KeystrokeCount++;
        QueryChanged.Trigger(new SearchQuery(text), caller: this);
    }

    /// <summary>
    /// Raises the pending query now instead of after the quiet period, and waits for its handlers.
    /// </summary>
    /// <returns><see langword="false"/> when no query was pending.</returns>
    public Task<bool> SubmitAsync()
    {
        return QueryChanged.FlushAsync();
    }

    public async Task<IReadOnlyList<CatalogProduct>> FindAsync(string text, CancellationToken cancellationToken)
    {
        await Task.Delay(SimulatedSearchLatency, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        var trimmedText = text.Trim();
        return Catalog
            .Where(product => product.Name.Contains(trimmedText, StringComparison.OrdinalIgnoreCase) ||
                product.Category.Contains(trimmedText, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    //disposing the event drops a query that is still waiting for its quiet period
    public ValueTask DisposeAsync()
    {
        return QueryChanged.DisposeAsync();
    }
}
