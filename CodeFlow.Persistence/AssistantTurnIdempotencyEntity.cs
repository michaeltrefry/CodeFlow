namespace CodeFlow.Persistence;

/// <summary>
/// sc-525 — Per-(conversation, idempotency_key) record of a single assistant turn so a
/// retried <c>POST /api/assistant/conversations/{id}/messages</c> doesn't double-persist
/// the user message or trigger a second LLM call. The row is created in <see cref="AssistantTurnIdempotencyStatus.InFlight"/>
/// before the user-message append; the recorded <see cref="EventsJson"/> stream is flushed
/// at terminal status so a duplicate request can replay it instead of running again.
/// </summary>
public sealed class AssistantTurnIdempotencyEntity
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>Client-supplied opaque key (UUID-shaped). Unique per ConversationId.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// Resolved caller id at claim time. Mismatch on a duplicate request means the same
    /// (conversation, key) is being reused across users — treated as a conflict by the endpoint.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the canonical request body. Mismatch → 409 conflict.</summary>
    public string RequestHash { get; set; } = string.Empty;

    public AssistantTurnIdempotencyStatus Status { get; set; }

    /// <summary>
    /// JSON array of recorded SSE frames in order: <c>[{"event":"text-delta","data":"…"}, …]</c>.
    /// Flushed once at terminal status so replay sees the full record. Connection-level
    /// housekeeping (the leading <c>: connected</c> heartbeat, the trailing <c>done</c> frame)
    /// is intentionally NOT recorded — replay regenerates it around the payload events.
    /// </summary>
    public string EventsJson { get; set; } = "[]";

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
}

public enum AssistantTurnIdempotencyStatus
{
    InFlight = 0,
    Completed = 1,
    Failed = 2,
}
