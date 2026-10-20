using Microsoft.AspNetCore.Http.HttpResults;

namespace IntercomEventing.Samples.WebApi.Features.Store;

public sealed record ChangeStoreStatusRequest(StoreStatus Status, string Reason);

public sealed record StoreStatusResponse(StoreStatus Status, bool Changed);

public static class StoreEndpoints
{
    public static IEndpointRouteBuilder MapStoreEndpoints(this IEndpointRouteBuilder app)
    {
        var store = app.MapGroup("/store");
        store.MapGet("/status", GetStatus);
        store.MapPut("/status", ChangeStatusAsync);
        return app;
    }

    private static Ok<StoreStatusResponse> GetStatus(StoreService store)
    {
        return TypedResults.Ok(new StoreStatusResponse(store.CurrentStatus, Changed: false));
    }

    private static async Task<Ok<StoreStatusResponse>> ChangeStatusAsync(ChangeStoreStatusRequest request, StoreService store)
    {
        //completes once every subscriber (the audit trail, open live-feed streams) has handled the change
        var changed = await store.ChangeStatusAsync(request.Status, request.Reason);
        return TypedResults.Ok(new StoreStatusResponse(store.CurrentStatus, changed));
    }
}
