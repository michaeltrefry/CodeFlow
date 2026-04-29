namespace CodeFlow.Persistence;

public interface IAssistantConversationRepository
{
    Task<AssistantConversation> GetOrCreateAsync(
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
}
