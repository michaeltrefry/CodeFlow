using CodeFlow.Persistence;
using CodeFlow.Persistence.Authority;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Endpoints;

/// <summary>
/// Implementation of <c>GET /api/traces/{id}/refusals</c>. Returns the append-only stream of
/// denied / skipped / preflight-blocked work captured by <see cref="EfRefusalEventSink"/> for
/// a workflow trace or assistant conversation. The trace inspector renders these as a
/// "Refusals" tab so operators can see what the run was prevented from doing rather than
/// inferring it from missing execution. See sc-285.
/// </summary>
public static class TraceRefusalsEndpoints
{
    public static async Task<IResult> GetTraceRefusalsAsync(
        Guid id,
        CodeFlowDbContext dbContext,
        IRefusalEventRepository refusals,
        CancellationToken cancellationToken)
    {
        // Mirror the token-usage endpoint's existence check: a trace id is "real" iff it
        // matches either a workflow saga or an assistant conversation's synthetic id.
        // Otherwise return 404 so the inspector cannot be probed for arbitrary guids.
        var sagaExists = await dbContext.WorkflowSagas
            .AsNoTracking()
            .AnyAsync(s => s.TraceId == id, cancellationToken);

        var assistantConversationId = sagaExists
            ? (Guid?)null
            : await dbContext.AssistantConversations
                .AsNoTracking()
                .Where(c => c.SyntheticTraceId == id)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(cancellationToken);

        if (!sagaExists && assistantConversationId is null)
        {
            return Results.NotFound();
        }

        var events = sagaExists
            ? await refusals.ListByTraceAsync(id, cancellationToken)
            : await refusals.ListByAssistantConversationAsync(assistantConversationId!.Value, cancellationToken);

        return Results.Ok(events);
    }
}
