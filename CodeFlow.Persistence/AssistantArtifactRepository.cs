using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public sealed class AssistantArtifactRepository(CodeFlowDbContext dbContext) : IAssistantArtifactRepository
{
    public async Task<AssistantArtifactEvent> AddAsync(
        Guid conversationId,
        ArtifactEventKind kind,
        string name,
        string relativePath,
        Guid? snapshotId,
        string? summaryJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        // Single SaveChanges; the (conversation_id, sequence) unique index catches a racer that
        // computed the same next value. Callers that hit the unique-violation path can retry —
        // we don't auto-retry here because the assistant turn is single-threaded per conversation.
        var conversation = await dbContext.AssistantConversations
            .SingleOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            ?? throw new InvalidOperationException($"Assistant conversation '{conversationId}' does not exist.");

        var nextSequence = (await dbContext.AssistantArtifactEvents
            .Where(e => e.ConversationId == conversationId)
            .Select(e => (int?)e.Sequence)
            .MaxAsync(cancellationToken)) + 1 ?? 1;

        var now = DateTime.UtcNow;
        var entity = new AssistantArtifactEventEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            MessageId = null,
            Sequence = nextSequence,
            Kind = kind,
            Name = name,
            RelativePath = relativePath,
            SnapshotId = snapshotId,
            SummaryJson = summaryJson,
            SupersededByEventId = null,
            ExpiredAtUtc = null,
            CreatedAtUtc = now,
        };

        dbContext.AssistantArtifactEvents.Add(entity);
        conversation.UpdatedAtUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return Map(entity);
    }

    public async Task<IReadOnlyList<AssistantArtifactEvent>> ListByConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.AssistantArtifactEvents
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<AssistantArtifactEvent?> GetAsync(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AssistantArtifactEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == eventId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<int> MarkActiveSupersededByNameAsync(
        Guid conversationId,
        string name,
        Guid supersedingEventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var actives = await dbContext.AssistantArtifactEvents
            .Where(e =>
                e.ConversationId == conversationId
                && e.Name == name
                && e.SupersededByEventId == null
                && e.ExpiredAtUtc == null
                && e.Id != supersedingEventId)
            .ToListAsync(cancellationToken);

        if (actives.Count == 0)
        {
            return 0;
        }

        foreach (var entity in actives)
        {
            entity.SupersededByEventId = supersedingEventId;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return actives.Count;
    }

    public async Task<int> MarkExpiredBySnapshotIdAsync(
        Guid snapshotId,
        DateTime expiredAtUtc,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AssistantArtifactEvents
            .SingleOrDefaultAsync(
                e => e.SnapshotId == snapshotId && e.ExpiredAtUtc == null,
                cancellationToken);

        if (entity is null)
        {
            return 0;
        }

        entity.ExpiredAtUtc = DateTime.SpecifyKind(expiredAtUtc, DateTimeKind.Utc);
        await dbContext.SaveChangesAsync(cancellationToken);
        return 1;
    }

    public async Task BindMessageAsync(
        Guid eventId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AssistantArtifactEvents
            .SingleOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        if (entity.MessageId == messageId)
        {
            return;
        }

        entity.MessageId = messageId;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static AssistantArtifactEvent Map(AssistantArtifactEventEntity entity) => new(
        Id: entity.Id,
        ConversationId: entity.ConversationId,
        MessageId: entity.MessageId,
        Sequence: entity.Sequence,
        Kind: entity.Kind,
        Name: entity.Name,
        RelativePath: entity.RelativePath,
        SnapshotId: entity.SnapshotId,
        SummaryJson: entity.SummaryJson,
        SupersededByEventId: entity.SupersededByEventId,
        ExpiredAtUtc: entity.ExpiredAtUtc is null
            ? null
            : DateTime.SpecifyKind(entity.ExpiredAtUtc.Value, DateTimeKind.Utc),
        CreatedAtUtc: DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc));
}
