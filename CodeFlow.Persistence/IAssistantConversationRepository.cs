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
}
