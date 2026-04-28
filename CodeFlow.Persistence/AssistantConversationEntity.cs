namespace CodeFlow.Persistence;

public sealed class AssistantConversationEntity
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public AssistantConversationScopeKind ScopeKind { get; set; }

    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    /// <summary>
    /// Derived from (ScopeKind, EntityType, EntityId). Non-null so the unique index against
    /// (user_id, scope_key) gives us at-most-one conversation per scope per user.
    /// </summary>
    public string ScopeKey { get; set; } = string.Empty;

    /// <summary>
    /// Stable per-conversation trace id used as the <c>TraceId</c> on captured
    /// <see cref="TokenUsageRecord"/> rows so assistant token usage threads into the same
    /// aggregation infrastructure as workflow saga traces.
    /// </summary>
    public Guid SyntheticTraceId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class AssistantMessageEntity
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>1-based, monotonic per conversation. Unique with ConversationId.</summary>
    public int Sequence { get; set; }

    public AssistantMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public string? Provider { get; set; }

    public string? Model { get; set; }

    /// <summary>Set on assistant turns to correlate with the captured <see cref="TokenUsageRecord"/>.</summary>
    public Guid? InvocationId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
