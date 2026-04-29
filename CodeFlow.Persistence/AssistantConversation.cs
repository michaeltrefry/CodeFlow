namespace CodeFlow.Persistence;

public sealed record AssistantConversation(
    Guid Id,
    string UserId,
    AssistantConversationScopeKind ScopeKind,
    string? EntityType,
    string? EntityId,
    string ScopeKey,
    Guid SyntheticTraceId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public AssistantConversationScope Scope => ScopeKind switch
    {
        AssistantConversationScopeKind.Homepage => AssistantConversationScope.Homepage(),
        AssistantConversationScopeKind.Entity => AssistantConversationScope.Entity(EntityType!, EntityId!),
        _ => throw new InvalidOperationException($"Unhandled scope kind '{ScopeKind}'.")
    };
}

public sealed record AssistantMessage(
    Guid Id,
    Guid ConversationId,
    int Sequence,
    AssistantMessageRole Role,
    string Content,
    string? Provider,
    string? Model,
    Guid? InvocationId,
    DateTime CreatedAtUtc);

/// <summary>
/// HAA-14 — Slim projection used by the resume-conversation rail. Carries the conversation's
/// stable identifiers + scope plus the first user-message preview (truncated server-side) so
/// the rail can render a meaningful label without fetching the full message body.
/// </summary>
/// <param name="MessageCount">Number of messages persisted on the conversation. Lets the UI
/// distinguish "fresh, never used" homepage threads (count = 0) from active ones.</param>
/// <param name="FirstUserMessagePreview">Trimmed first-user-message snippet (max ~120 chars).
/// Null when the conversation has no user message yet.</param>
public sealed record AssistantConversationSummary(
    Guid Id,
    string UserId,
    AssistantConversationScopeKind ScopeKind,
    string? EntityType,
    string? EntityId,
    string ScopeKey,
    Guid SyntheticTraceId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    int MessageCount,
    string? FirstUserMessagePreview);
