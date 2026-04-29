namespace CodeFlow.Persistence;

public sealed class AssistantConversationEntity
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public AssistantConversationScopeKind ScopeKind { get; set; }

    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    /// <summary>
    /// Derived from (ScopeKind, EntityType, EntityId). Used to find the most recent
    /// conversation for a user + scope while still allowing older threads to be preserved.
    /// </summary>
    public string ScopeKey { get; set; } = string.Empty;

    /// <summary>
    /// Stable per-conversation trace id used as the <c>TraceId</c> on captured
    /// <see cref="TokenUsageRecord"/> rows so assistant token usage threads into the same
    /// aggregation infrastructure as workflow saga traces.
    /// </summary>
    public Guid SyntheticTraceId { get; set; }

    /// <summary>
    /// HAA-17 — Cumulative input tokens captured against this conversation across every assistant
    /// turn. Updated alongside <see cref="TokenUsageRecordEntity"/> writes so the UI can render
    /// a live total without re-aggregating per turn, and so the
    /// <see cref="AssistantSettingsEntity.MaxTokensPerConversation"/> cap can be enforced cheaply.
    /// </summary>
    public long InputTokensTotal { get; set; }

    /// <summary>HAA-17 — Cumulative output tokens; mirrors <see cref="InputTokensTotal"/>.</summary>
    public long OutputTokensTotal { get; set; }

    /// <summary>
    /// HAA-19 — Signature of the workspace the assistant most recently used for this conversation
    /// (e.g. <c>conversation</c> or <c>trace:{guidN}</c>). Used to detect when the next turn's
    /// resolved workspace differs and the model needs a one-shot "workspace switched" notice in
    /// its system prompt so it can re-orient. Null on first turn / before the column existed.
    /// </summary>
    public string? ActiveWorkspaceSignature { get; set; }

    /// <summary>
    /// Auto-compaction watermark. Messages with <c>Sequence ≤ CompactedThroughSequence</c> have
    /// been replaced by a synthesized <see cref="AssistantMessageRole.Summary"/> message and are
    /// excluded from the outgoing LLM history; they remain in the table for transcript display
    /// and audit. Zero means "no compaction has happened yet".
    /// </summary>
    public int CompactedThroughSequence { get; set; }

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
