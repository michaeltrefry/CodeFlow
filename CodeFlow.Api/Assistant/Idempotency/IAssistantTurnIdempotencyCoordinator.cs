using CodeFlow.Persistence;

namespace CodeFlow.Api.Assistant.Idempotency;

/// <summary>
/// sc-525 — Glue between the assistant POST endpoint and the idempotency repository. Owns
/// the dispatch decision (claim / replay / wait-then-replay / conflict) and the in-process
/// signal pathway so duplicate retries against the same instance unblock immediately when
/// the originating turn completes.
/// </summary>
public interface IAssistantTurnIdempotencyCoordinator
{
    Task<AssistantTurnDispatchOutcome> DispatchAsync(
        Guid conversationId,
        string idempotencyKey,
        string userId,
        string requestHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// Polls until the row reaches a terminal status or the timeout elapses, opportunistically
    /// awaiting the in-process <see cref="AssistantTurnSignalRegistry"/> so same-instance
    /// completions wake the waiter without DB latency.
    /// </summary>
    Task<AssistantTurnIdempotencyRecord> WaitForTerminalAsync(
        Guid recordId,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Walks the recorded events on <paramref name="record"/> and writes each one to
    /// <paramref name="writeFrame"/>. Connection-level housekeeping (the <c>: connected</c>
    /// heartbeat and the trailing <c>done</c> frame) is the caller's responsibility.
    /// </summary>
    Task ReplayAsync(
        AssistantTurnIdempotencyRecord record,
        Func<string, string, CancellationToken, Task> writeFrame,
        CancellationToken cancellationToken);
}

public abstract record AssistantTurnDispatchOutcome
{
    /// <summary>First request for this key — endpoint runs the turn, recording events through
    /// <see cref="Recorder"/>.</summary>
    public sealed record Claimed(
        AssistantTurnIdempotencyRecord Record,
        IAssistantTurnRecorder Recorder) : AssistantTurnDispatchOutcome;

    /// <summary>Same key, different request body. 409 conflict.</summary>
    public sealed record HashMismatch(AssistantTurnIdempotencyRecord Existing) : AssistantTurnDispatchOutcome;

    /// <summary>Same key, different caller. 404 — don't leak existence.</summary>
    public sealed record UserMismatch(AssistantTurnIdempotencyRecord Existing) : AssistantTurnDispatchOutcome;

    /// <summary>Duplicate, originating turn already terminal. Replay events.</summary>
    public sealed record Replay(AssistantTurnIdempotencyRecord Record) : AssistantTurnDispatchOutcome;

    /// <summary>Duplicate, originating turn still in flight. Wait, then replay.</summary>
    public sealed record WaitThenReplay(AssistantTurnIdempotencyRecord Record) : AssistantTurnDispatchOutcome;
}

/// <summary>
/// Captures SSE frame writes during the originating turn so they can be replayed to a
/// retried request. <see cref="FlushAsync"/> persists the captured stream + terminal status
/// and signals any in-process waiter.
/// </summary>
public interface IAssistantTurnRecorder
{
    Guid RecordId { get; }
    void Record(string eventName, string payload);
    Task FlushAsync(AssistantTurnIdempotencyStatus terminalStatus, CancellationToken cancellationToken);
}
