using CodeFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Orchestration.TokenTracking;

/// <summary>
/// Resolves <c>(rootTraceId, scopeChain)</c> for a token-usage record by walking
/// <see cref="WorkflowSagaStateEntity.ParentTraceId"/> from the current saga toward the root.
/// </summary>
/// <remarks>
/// <para>
/// Convention applied across the Token Usage Tracking epic:
/// <list type="bullet">
///   <item><c>TraceId</c> stored on the record is always the root saga's TraceId.</item>
///   <item><c>ScopeChain</c> stores intermediate-and-current saga ids, ordered root→leaf,
///   excluding the root. For top-level sagas this is empty.</item>
/// </list>
/// </para>
/// <para>
/// Subflow depth is capped at 3 (<see cref="WorkflowSagaStateEntity.SubflowDepth"/>), so this
/// performs at most 3 lookups per consumer call. Each lookup hits the saga DbSet by TraceId,
/// returning a flat row — no joins, no eager loads.
/// </para>
/// </remarks>
public static class SagaScopeChainResolver
{
    /// <summary>
    /// Cap the walk so a corrupted parent chain (e.g., a row pointing at itself) cannot stall
    /// a consumer call indefinitely. Higher than the configured subflow depth limit (3) by an
    /// order of magnitude — overflow always indicates a bug, not legitimate nesting.
    /// </summary>
    private const int MaxWalkDepth = 32;

    public static async Task<SagaScope> ResolveAsync(
        CodeFlowDbContext dbContext,
        Guid currentTraceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        // Walk the parent chain from current → root. AsNoTracking because we never mutate the
        // saga rows from the capture path; tracking would pollute the consumer's change set.
        var chain = new List<Guid> { currentTraceId };
        var lookupTraceId = currentTraceId;

        for (var step = 0; step < MaxWalkDepth; step++)
        {
            var saga = await dbContext.WorkflowSagas
                .AsNoTracking()
                .Where(s => s.TraceId == lookupTraceId)
                .Select(s => new { s.ParentTraceId })
                .SingleOrDefaultAsync(cancellationToken);

            // No row at all (top-level saga not yet persisted, or unknown trace) — treat the
            // current trace as the root with an empty scope chain. Failing the walk here would
            // drop a TokenUsageRecord that the user can recover post-hoc; better to write a
            // record attributed to the only TraceId we know.
            if (saga is null || saga.ParentTraceId is null)
            {
                break;
            }

            lookupTraceId = saga.ParentTraceId.Value;
            chain.Add(lookupTraceId);
        }

        chain.Reverse(); // chain[0] is now root, chain[^1] is current
        var rootTraceId = chain[0];
        var scopeChain = chain.Count > 1
            ? chain.Skip(1).ToArray()
            : Array.Empty<Guid>();

        return new SagaScope(rootTraceId, scopeChain);
    }
}

public sealed record SagaScope(Guid RootTraceId, IReadOnlyList<Guid> ScopeChain);
