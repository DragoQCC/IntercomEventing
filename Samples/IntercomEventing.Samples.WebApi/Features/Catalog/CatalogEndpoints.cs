using Microsoft.AspNetCore.Http.HttpResults;

namespace IntercomEventing.Samples.WebApi.Features.Catalog;

public sealed record UpdatePriceRequest(decimal Price);

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/products");
        products.MapGet("/", GetProducts);
        products.MapGet("/price-list", GetPublishedPriceList);
        products.MapPut("/{sku}/price", UpdatePrice);
        return app;
    }

    private static Ok<IReadOnlyList<Product>> GetProducts(CatalogService catalog)
    {
        return TypedResults.Ok(catalog.Products);
    }

    private static Ok<PublishedPriceList> GetPublishedPriceList(PriceListPublisher publisher)
    {
        return TypedResults.Ok(publisher.Current);
    }

    private static Results<Ok<Product>, NotFound, ValidationProblem> UpdatePrice(string sku, UpdatePriceRequest request, CatalogService catalog)
    {
        if (request.Price <= 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["price"] = ["The price must be positive."] });
        }
        //returns at once: the price list is republished in the background once the updates stop
        var updatedProduct = catalog.UpdatePrice(sku, request.Price);
        if (updatedProduct is null)
        {
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(updatedProduct);
    }
}
