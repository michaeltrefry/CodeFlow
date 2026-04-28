namespace CodeFlow.Persistence;

public interface IAssistantConversationRepository
{
    Task<AssistantConversation> GetOrCreateAsync(
        string userId,
        AssistantConversationScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a fresh, ephemeral demo-mode conversation tagged with a synthetic anonymous
    /// user id (see <see cref="AnonymousAssistantUser"/>). Each call inserts a new row — there
    /// is no dedupe because the homepage isn't authenticated and we have no stable per-visitor
    /// identifier; the returned conversation's guid is the only handle the caller has.
    /// </summary>
    Task<AssistantConversation> CreateAnonymousAsync(
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
}
