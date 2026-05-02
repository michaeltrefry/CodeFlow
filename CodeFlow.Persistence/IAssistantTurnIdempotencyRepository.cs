namespace CodeFlow.Persistence;

/// <summary>
/// sc-525 — Per-turn idempotency record store. Backs the retry-safe assistant-turn POST:
/// the endpoint claims a row with <see cref="TryClaimAsync"/>, runs the turn while
/// recording events into an in-memory buffer, then flushes the buffer with
/// <see cref="MarkTerminalAsync"/>. Duplicate requests <see cref="GetAsync"/> the row to
/// decide between replaying recorded events or polling for an in-flight turn to finish.
/// </summary>
public interface IAssistantTurnIdempotencyRepository
{
    /// <summary>
    /// Inserts a new <see cref="AssistantTurnIdempotencyStatus.InFlight"/> row. If a row
    /// already exists for <paramref name="conversationId"/> + <paramref name="idempotencyKey"/>,
    /// returns it as <see cref="AssistantTurnClaimOutcome.Existing"/> so the endpoint can
    /// validate hash/user and replay or wait. The unique index on
    /// (conversation_id, idempotency_key) makes the duplicate detection authoritative.
    /// </summary>
    Task<AssistantTurnClaimOutcome> TryClaimAsync(
        Guid conversationId,
        string idempotencyKey,
        string userId,
        string requestHash,
        DateTime nowUtc,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    Task<AssistantTurnIdempotencyRecord?> GetAsync(
        Guid conversationId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<AssistantTurnIdempotencyRecord?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the recorded event stream and the terminal status. Called once when the
    /// originating turn finishes (Completed) or throws/cancels (Failed). Stale InFlight rows
    /// that never reach this call are reaped by the background sweep via TTL expiry.
    /// </summary>
    Task MarkTerminalAsync(
        Guid id,
        AssistantTurnIdempotencyStatus terminalStatus,
        string eventsJson,
        DateTime completedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes rows where <c>expires_at &lt;= nowUtc</c>. Returns the deleted-row count for
    /// logging. Used by the background sweep service.
    /// </summary>
    Task<int> PurgeExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}

public abstract record AssistantTurnClaimOutcome
{
    public sealed record Claimed(AssistantTurnIdempotencyRecord Record) : AssistantTurnClaimOutcome;
    public sealed record Existing(AssistantTurnIdempotencyRecord Record) : AssistantTurnClaimOutcome;
}

public sealed record AssistantTurnIdempotencyRecord(
    Guid Id,
    Guid ConversationId,
    string IdempotencyKey,
    string UserId,
    string RequestHash,
    AssistantTurnIdempotencyStatus Status,
    string EventsJson,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime ExpiresAtUtc);
