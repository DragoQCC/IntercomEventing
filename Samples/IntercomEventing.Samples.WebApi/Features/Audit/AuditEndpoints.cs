using Microsoft.AspNetCore.Http.HttpResults;

namespace IntercomEventing.Samples.WebApi.Features.Audit;

public sealed record AuditOverview(int PendingEntries, IReadOnlyList<AuditBatch> Batches);

public sealed record AuditFlushResult(bool Flushed);

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var audit = app.MapGroup("/audit");
        audit.MapGet("/", GetAudit);
        audit.MapPost("/flush", FlushAsync);
        return app;
    }

    private static Ok<AuditOverview> GetAudit(AuditTrail trail, AuditArchive archive)
    {
        return TypedResults.Ok(new AuditOverview(trail.PendingEntryCount, archive.Batches));
    }

    private static async Task<Ok<AuditFlushResult>> FlushAsync(AuditTrail trail)
    {
        var flushed = await trail.FlushNowAsync();
        return TypedResults.Ok(new AuditFlushResult(flushed));
    }
}
