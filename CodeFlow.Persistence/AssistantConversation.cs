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
