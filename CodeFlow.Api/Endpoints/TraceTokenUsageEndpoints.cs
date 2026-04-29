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
        // The token-usage table is shared between workflow saga traces and synthetic assistant
        // conversation traces (HAA-1 routes assistant invocations through the same capture
        // pipeline). A trace id is "real" iff it matches either a saga row OR an assistant
        // conversation's synthetic id; otherwise return 404 so the inspector can't be probed for
        // arbitrary guids. The stream kind on the response (HAA-14) tells the panel which label
        // to render — workflow run or Assistant.
        var sagaExists = await dbContext.WorkflowSagas
            .AsNoTracking()
            .AnyAsync(s => s.TraceId == id, cancellationToken);

        var assistantExists = !sagaExists && await dbContext.AssistantConversations
            .AsNoTracking()
            .AnyAsync(c => c.SyntheticTraceId == id, cancellationToken);

        if (!sagaExists && !assistantExists)
        {
            return Results.NotFound();
        }

        var streamKind = sagaExists
            ? TokenUsageAggregator.WorkflowStreamKind
            : TokenUsageAggregator.AssistantStreamKind;

        var records = await tokenUsageRecords.ListByTraceAsync(id, cancellationToken);
        var aggregated = TokenUsageAggregator.Aggregate(id, records, streamKind);
        return Results.Ok(aggregated);
    }
}
