using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public sealed class AssistantConversationRepository(CodeFlowDbContext dbContext) : IAssistantConversationRepository
{
    public async Task<AssistantConversation> GetOrCreateAsync(
        string userId,
        AssistantConversationScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(scope);

        var trimmedUserId = userId.Trim();
        var scopeKey = scope.ToScopeKey();

        var existing = await dbContext.AssistantConversations
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.UserId == trimmedUserId && c.ScopeKey == scopeKey, cancellationToken);

        if (existing is not null)
        {
            return Map(existing);
        }

        var now = DateTime.UtcNow;
        var entity = new AssistantConversationEntity
        {
            Id = Guid.NewGuid(),
            UserId = trimmedUserId,
            ScopeKind = scope.Kind,
            EntityType = scope.EntityType,
            EntityId = scope.EntityId,
            ScopeKey = scopeKey,
            SyntheticTraceId = Guid.NewGuid(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        dbContext.AssistantConversations.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent get-or-create race: another request inserted the same (user, scope_key)
            // between our SELECT and INSERT. Re-read and return the winner.
            dbContext.AssistantConversations.Remove(entity);
            var winner = await dbContext.AssistantConversations
                .AsNoTracking()
                .SingleAsync(c => c.UserId == trimmedUserId && c.ScopeKey == scopeKey, cancellationToken);
            return Map(winner);
        }

        return Map(entity);
    }

    public async Task<AssistantConversation?> GetByIdAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AssistantConversations
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<AssistantMessage>> ListMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.AssistantMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<AssistantMessage> AppendMessageAsync(
        Guid conversationId,
        AssistantMessageRole role,
        string content,
        string? provider,
        string? model,
        Guid? invocationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var conversation = await dbContext.AssistantConversations
            .SingleOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            ?? throw new InvalidOperationException($"Assistant conversation '{conversationId}' does not exist.");

        var nextSequence = (await dbContext.AssistantMessages
            .Where(m => m.ConversationId == conversationId)
            .Select(m => (int?)m.Sequence)
            .MaxAsync(cancellationToken)) + 1 ?? 1;

        var now = DateTime.UtcNow;
        var entity = new AssistantMessageEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Sequence = nextSequence,
            Role = role,
            Content = content,
            Provider = provider,
            Model = model,
            InvocationId = invocationId,
            CreatedAtUtc = now,
        };

        dbContext.AssistantMessages.Add(entity);
        conversation.UpdatedAtUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return Map(entity);
    }

    public async Task<IReadOnlyList<AssistantConversationSummary>> ListByUserAsync(
        string userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // Defensive — caller is expected to clamp, but guard against accidental zero/negative
        // limits silently returning empty.
        if (limit <= 0)
        {
            return Array.Empty<AssistantConversationSummary>();
        }

        var trimmedUserId = userId.Trim();

        // Single round-trip: pull the most recent N conversations for the user, plus aggregate
        // message stats (count + earliest user-message body) per conversation. Done as a left
        // join so conversations with zero messages still surface (a brand-new homepage thread
        // exists in the DB the moment the user lands on the page; the rail still shows it as
        // "Homepage").
        var rows = await (
            from c in dbContext.AssistantConversations.AsNoTracking()
            where c.UserId == trimmedUserId
            orderby c.UpdatedAtUtc descending, c.Id
            select new
            {
                Conversation = c,
                MessageCount = dbContext.AssistantMessages.Count(m => m.ConversationId == c.Id),
                FirstUserMessage = dbContext.AssistantMessages
                    .Where(m => m.ConversationId == c.Id && m.Role == AssistantMessageRole.User)
                    .OrderBy(m => m.Sequence)
                    .Select(m => m.Content)
                    .FirstOrDefault()
            })
            .Take(limit)
            .ToListAsync(cancellationToken);

        var result = new AssistantConversationSummary[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            result[i] = new AssistantConversationSummary(
                Id: row.Conversation.Id,
                UserId: row.Conversation.UserId,
                ScopeKind: row.Conversation.ScopeKind,
                EntityType: row.Conversation.EntityType,
                EntityId: row.Conversation.EntityId,
                ScopeKey: row.Conversation.ScopeKey,
                SyntheticTraceId: row.Conversation.SyntheticTraceId,
                CreatedAtUtc: DateTime.SpecifyKind(row.Conversation.CreatedAtUtc, DateTimeKind.Utc),
                UpdatedAtUtc: DateTime.SpecifyKind(row.Conversation.UpdatedAtUtc, DateTimeKind.Utc),
                MessageCount: row.MessageCount,
                FirstUserMessagePreview: TruncatePreview(row.FirstUserMessage));
        }

        return result;
    }

    private const int PreviewCharLimit = 120;

    private static string? TruncatePreview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        // Collapse whitespace + cap length so the rail can render a clean single line. We only
        // keep the leading window — the rail uses this purely to disambiguate threads.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(content.Trim(), @"\s+", " ");
        return collapsed.Length <= PreviewCharLimit
            ? collapsed
            : collapsed[..PreviewCharLimit] + "…";
    }

    private static AssistantConversation Map(AssistantConversationEntity entity) => new(
        Id: entity.Id,
        UserId: entity.UserId,
        ScopeKind: entity.ScopeKind,
        EntityType: entity.EntityType,
        EntityId: entity.EntityId,
        ScopeKey: entity.ScopeKey,
        SyntheticTraceId: entity.SyntheticTraceId,
        CreatedAtUtc: DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc),
        UpdatedAtUtc: DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc));

    private static AssistantMessage Map(AssistantMessageEntity entity) => new(
        Id: entity.Id,
        ConversationId: entity.ConversationId,
        Sequence: entity.Sequence,
        Role: entity.Role,
        Content: entity.Content,
        Provider: entity.Provider,
        Model: entity.Model,
        InvocationId: entity.InvocationId,
        CreatedAtUtc: DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc));
}
