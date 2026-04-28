namespace CodeFlow.Persistence;

public enum AssistantConversationScopeKind
{
    Homepage = 0,
    Entity = 1
}

/// <summary>
/// Identifies a single assistant conversation slot for a given user. Either the open homepage
/// conversation or an entity-scoped conversation (e.g. a specific trace, workflow, or node) so
/// returning to the same entity resumes the prior thread.
/// </summary>
public sealed record AssistantConversationScope(
    AssistantConversationScopeKind Kind,
    string? EntityType,
    string? EntityId)
{
    public static AssistantConversationScope Homepage()
        => new(AssistantConversationScopeKind.Homepage, null, null);

    public static AssistantConversationScope Entity(string entityType, string entityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        return new AssistantConversationScope(AssistantConversationScopeKind.Entity, entityType.Trim(), entityId.Trim());
    }

    /// <summary>
    /// Stable string key used as the user-scoped uniqueness anchor in storage. MySQL treats NULL
    /// values in unique indexes as distinct, so we collapse the (kind, entityType, entityId) tuple
    /// into a single non-null derived key column.
    /// </summary>
    public string ToScopeKey() => Kind switch
    {
        AssistantConversationScopeKind.Homepage => "homepage",
        AssistantConversationScopeKind.Entity => $"entity:{EntityType}:{EntityId}",
        _ => throw new InvalidOperationException($"Unhandled scope kind '{Kind}'.")
    };
}
