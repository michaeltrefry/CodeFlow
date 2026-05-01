namespace CodeFlow.Persistence;

public interface IAssistantConversationRepository
{
    Task<AssistantConversation> GetOrCreateAsync(
        string userId,
        AssistantConversationScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a fresh conversation for the user and scope even when older conversations exist.
    /// </summary>
    Task<AssistantConversation> CreateAsync(
        string userId,
        AssistantConversationScope scope,
        CancellationToken cancellationToken = default);

    Task<AssistantConversation?> GetByIdAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssistantMessage>> ListMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a single message to the conversation. The repository assigns the next monotonic
    /// sequence number atomically and bumps <c>UpdatedAtUtc</c> on the parent conversation. Caller
    /// is responsible for ordering its own writes (one append at a time per conversation).
    /// </summary>
    Task<AssistantMessage> AppendMessageAsync(
        Guid conversationId,
        AssistantMessageRole role,
        string content,
        string? provider,
        string? model,
        Guid? invocationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// HAA-14 — Lists the user's recent conversations (homepage + entity-scoped) ordered by
    /// most recently updated first. Each row includes a short preview of the conversation's
    /// first user message so the resume-conversation rail can render meaningful labels without
    /// fetching every message body. Conversations with no user messages yet still surface (the
    /// homepage thread is created on first visit) — preview is null in that case so the rail
    /// can fall back to a default label.
    /// </summary>
    /// <param name="userId">Resolved user id (real claims subject or anonymous demo id).</param>
    /// <param name="limit">Maximum rows to return. Caller is responsible for clamping.</param>
    Task<IReadOnlyList<AssistantConversationSummary>> ListByUserAsync(
        string userId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// HAA-17 — Atomically increment the conversation's cumulative input/output token totals
    /// and return the new totals. Called once per captured <c>TokenUsageRecord</c> so the live
    /// chip + the per-conversation cap have a cheap, persisted source of truth without
    /// re-aggregating every turn. Negative deltas are clamped to zero.
    /// </summary>
    Task<AssistantConversationTokenTotals> AddTokenUsageAsync(
        Guid conversationId,
        long inputTokensDelta,
        long outputTokensDelta,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// HAA-19 — Persists the workspace signature the assistant just used for this conversation.
    /// On the next turn, the chat service compares the new resolved signature against this value
    /// to decide whether to inject a one-shot "workspace switched" notice into the system prompt.
    /// </summary>
    Task SetActiveWorkspaceSignatureAsync(
        Guid conversationId,
        string? signature,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new conversation owned by the same user under the same scope as
    /// <paramref name="sourceConversationId"/>, copying every message up to and including
    /// <paramref name="throughMessageId"/>. Sequence numbers, roles, content, provider/model and
    /// invocation ids are preserved on the copies; ids and timestamps are minted fresh. Token
    /// totals on the new conversation start at zero so the live chip reflects the forked turn
    /// budget rather than the source's history. Returns null when either id does not resolve or
    /// when the message does not belong to the source conversation.
    /// </summary>
    Task<AssistantConversationFork?> ForkAsync(
        Guid sourceConversationId,
        Guid throughMessageId,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of a successful <see cref="IAssistantConversationRepository.ForkAsync"/> call.</summary>
public sealed record AssistantConversationFork(
    AssistantConversation Conversation,
    IReadOnlyList<AssistantMessage> Messages);

public sealed record AssistantConversationTokenTotals(long InputTokensTotal, long OutputTokensTotal);
