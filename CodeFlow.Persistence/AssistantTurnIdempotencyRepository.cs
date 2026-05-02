using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public sealed class AssistantTurnIdempotencyRepository(CodeFlowDbContext dbContext)
    : IAssistantTurnIdempotencyRepository
{
    public async Task<AssistantTurnClaimOutcome> TryClaimAsync(
        Guid conversationId,
        string idempotencyKey,
        string userId,
        string requestHash,
        DateTime nowUtc,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        // Resolve duplicates with a pre-check first — avoids the unique-violation round-trip on
        // the common-case retry where we already have a row. The unique index still backs
        // correctness if two requests race here: the loser gets a DbUpdateException and falls
        // through to the same Existing path.
        var existing = await dbContext.AssistantTurnIdempotency
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.ConversationId == conversationId && e.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existing is not null)
        {
            return new AssistantTurnClaimOutcome.Existing(Map(existing));
        }

        var entity = new AssistantTurnIdempotencyEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            IdempotencyKey = idempotencyKey,
            UserId = userId,
            RequestHash = requestHash,
            Status = AssistantTurnIdempotencyStatus.InFlight,
            EventsJson = "[]",
            CreatedAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
            CompletedAtUtc = null,
            ExpiresAtUtc = DateTime.SpecifyKind(nowUtc.Add(ttl), DateTimeKind.Utc),
        };

        dbContext.AssistantTurnIdempotency.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new AssistantTurnClaimOutcome.Claimed(Map(entity));
        }
        catch (DbUpdateException)
        {
            // Concurrent claim won the race — detach our optimistic insert and read back the
            // winner. Letting the exception bubble would leave the change tracker in a bad state
            // for callers reusing this DbContext.
            dbContext.Entry(entity).State = EntityState.Detached;

            var winner = await dbContext.AssistantTurnIdempotency
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    e => e.ConversationId == conversationId && e.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return new AssistantTurnClaimOutcome.Existing(Map(winner));
        }
    }

    public async Task<AssistantTurnIdempotencyRecord?> GetAsync(
        Guid conversationId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var entity = await dbContext.AssistantTurnIdempotency
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.ConversationId == conversationId && e.IdempotencyKey == idempotencyKey,
                cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<AssistantTurnIdempotencyRecord?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AssistantTurnIdempotency
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task MarkTerminalAsync(
        Guid id,
        AssistantTurnIdempotencyStatus terminalStatus,
        string eventsJson,
        DateTime completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (terminalStatus == AssistantTurnIdempotencyStatus.InFlight)
        {
            throw new ArgumentException("Terminal status must be Completed or Failed.", nameof(terminalStatus));
        }

        var entity = await dbContext.AssistantTurnIdempotency
            .SingleOrDefaultAsync(e => e.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Idempotency record '{id}' not found.");

        entity.Status = terminalStatus;
        entity.EventsJson = eventsJson;
        entity.CompletedAtUtc = DateTime.SpecifyKind(completedAtUtc, DateTimeKind.Utc);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> PurgeExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return await dbContext.AssistantTurnIdempotency
            .Where(e => e.ExpiresAtUtc <= cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static AssistantTurnIdempotencyRecord Map(AssistantTurnIdempotencyEntity entity) => new(
        Id: entity.Id,
        ConversationId: entity.ConversationId,
        IdempotencyKey: entity.IdempotencyKey,
        UserId: entity.UserId,
        RequestHash: entity.RequestHash,
        Status: entity.Status,
        EventsJson: entity.EventsJson,
        CreatedAtUtc: DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc),
        CompletedAtUtc: entity.CompletedAtUtc.HasValue
            ? DateTime.SpecifyKind(entity.CompletedAtUtc.Value, DateTimeKind.Utc)
            : null,
        ExpiresAtUtc: DateTime.SpecifyKind(entity.ExpiresAtUtc, DateTimeKind.Utc));
}
