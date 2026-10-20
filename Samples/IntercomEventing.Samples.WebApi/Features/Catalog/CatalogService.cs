using System.Diagnostics.CodeAnalysis;

namespace IntercomEventing.Samples.WebApi.Features.Catalog;

public sealed record Product(string Sku, string Name, decimal Price);

public sealed record PriceChange(string Sku, decimal OldPrice, decimal NewPrice);

/// <summary>
/// The product catalog. A price update is visible at once in <see cref="Products"/>; the published price list follows
/// through a <see cref="DebounceEvent{TPayload}"/>, so a burst of updates republishes it once.
/// </summary>
public sealed class CatalogService : IAsyncDisposable
{
    //republish once price updates have stopped for this long
    private static readonly TimeSpan PriceChangeQuietPeriod = TimeSpan.FromSeconds(2);

    private readonly Lock _productsLock = new();
    private readonly Dictionary<string, Product> _products = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KB-01"] = new Product("KB-01", "Mechanical keyboard", 89.00m),
        ["MS-02"] = new Product("MS-02", "Wireless mouse", 39.00m),
        ["MN-27"] = new Product("MN-27", "27 inch monitor", 229.00m),
        ["DK-03"] = new Product("DK-03", "USB-C dock", 119.00m),
    };

    private readonly DebounceEvent<PriceChange> _pricesChanged = new(PriceChangeQuietPeriod) { Name = "PricesChanged" };

    public IIntercomEvent<DebounceEventCall<PriceChange>> PricesChanged => _pricesChanged;

    public IReadOnlyList<Product> Products
    {
        get
        {
            lock (_productsLock)
            {
                return _products.Values.OrderBy(product => product.Sku, StringComparer.Ordinal).ToArray();
            }
        }
    }

    public bool TryGetProduct(string sku, [NotNullWhen(true)] out Product? product)
    {
        lock (_productsLock)
        {
            return _products.TryGetValue(sku, out product);
        }
    }

    /// <returns>The updated product, or <see langword="null"/> when <paramref name="sku"/> is unknown.</returns>
    public Product? UpdatePrice(string sku, decimal newPrice)
    {
        PriceChange change;
        Product updatedProduct;
        lock (_productsLock)
        {
            if (!_products.TryGetValue(sku, out var currentProduct))
            {
                return null;
            }
            updatedProduct = currentProduct with { Price = newPrice };
            _products[currentProduct.Sku] = updatedProduct;
            change = new PriceChange(currentProduct.Sku, currentProduct.Price, newPrice);
        }
        //never waits; the raise carries the last change of the burst and how many changes it absorbed
        _pricesChanged.Trigger(change, caller: this);
        return updatedProduct;
    }

    //disposing the event drops a burst that is still waiting for its quiet period
    public ValueTask DisposeAsync()
    {
        return _pricesChanged.DisposeAsync();
    }
}
