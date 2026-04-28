using CodeFlow.Api.TokenTracking;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Endpoints;

/// <summary>
/// Implementation of <c>GET /api/traces/{id}/token-usage</c>. Returns rollups at every level
/// (per-call, per-invocation, per-node, per-scope, per-trace) for a single trace, computed on
/// read by <see cref="TokenUsageAggregator"/>. The trace inspector calls this once on open and
/// then layers in <c>TokenUsageRecorded</c> SSE events to keep live overlays current.
/// </summary>
public static class TraceTokenUsageEndpoints
{
    public static async Task<IResult> GetTraceTokenUsageAsync(
        Guid id,
        CodeFlowDbContext dbContext,
        ITokenUsageRecordRepository tokenUsageRecords,
        CancellationToken cancellationToken)
    {
        // 404 only when the trace itself doesn't exist; a trace that exists but hasn't issued an
        // LLM call yet returns a 200 with empty rollups so the inspector can render an empty-state
        // pane consistently.
        var traceExists = await dbContext.WorkflowSagas
            .AsNoTracking()
            .AnyAsync(s => s.TraceId == id, cancellationToken);
        if (!traceExists)
        {
            return Results.NotFound();
        }

        var records = await tokenUsageRecords.ListByTraceAsync(id, cancellationToken);
        var aggregated = TokenUsageAggregator.Aggregate(id, records);
        return Results.Ok(aggregated);
    }
}
