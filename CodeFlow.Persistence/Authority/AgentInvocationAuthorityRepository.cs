using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence.Authority;

public sealed class AgentInvocationAuthorityRepository : IAgentInvocationAuthorityRepository
{
    private readonly CodeFlowDbContext db;

    public AgentInvocationAuthorityRepository(CodeFlowDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        this.db = db;
    }

    public async Task<IReadOnlyList<AgentInvocationAuthorityEntity>> ListByTraceAsync(
        Guid traceId,
        CancellationToken cancellationToken = default)
    {
        return await db.AgentInvocationAuthority
            .AsNoTracking()
            .Where(e => e.TraceId == traceId)
            .OrderBy(e => e.ResolvedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentInvocationAuthorityEntity?> GetByRoundAsync(
        Guid traceId,
        Guid roundId,
        CancellationToken cancellationToken = default)
    {
        return await db.AgentInvocationAuthority
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.TraceId == traceId && e.RoundId == roundId,
                cancellationToken);
    }
}
