using CodeFlow.Runtime.Authority;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence.Authority;

public sealed class RefusalEventRepository : IRefusalEventRepository
{
    private readonly CodeFlowDbContext db;

    public RefusalEventRepository(CodeFlowDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        this.db = db;
    }

    public async Task<IReadOnlyList<RefusalEvent>> ListByTraceAsync(
        Guid traceId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.RefusalEvents
            .AsNoTracking()
            .Where(e => e.TraceId == traceId)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync(cancellationToken);

        return rows.Select(MapToContract).ToArray();
    }

    public async Task<IReadOnlyList<RefusalEvent>> ListByAssistantConversationAsync(
        Guid assistantConversationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.RefusalEvents
            .AsNoTracking()
            .Where(e => e.AssistantConversationId == assistantConversationId)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync(cancellationToken);

        return rows.Select(MapToContract).ToArray();
    }

    private static RefusalEvent MapToContract(RefusalEventEntity entity)
    {
        return new RefusalEvent(
            Id: entity.Id,
            TraceId: entity.TraceId,
            AssistantConversationId: entity.AssistantConversationId,
            Stage: entity.Stage,
            Code: entity.Code,
            Reason: entity.Reason,
            Axis: entity.Axis,
            Path: entity.Path,
            DetailJson: entity.DetailJson,
            OccurredAt: new DateTimeOffset(DateTime.SpecifyKind(entity.OccurredAtUtc, DateTimeKind.Utc)));
    }
}
