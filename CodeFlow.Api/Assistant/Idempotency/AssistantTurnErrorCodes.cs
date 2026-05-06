namespace CodeFlow.Api.Assistant.Idempotency;

/// <summary>
/// sc-806 — Stable identifiers for the SSE <c>error</c> frames the assistant turn endpoint
/// emits on the idempotent-retry paths. The chat panel keys off these codes to decide which
/// affordances (Retry / Cancel) the error banner surfaces; new codes added here MUST land
/// alongside a chat-panel update in the same commit.
/// </summary>
public static class AssistantTurnErrorCodes
{
    /// <summary>
    /// The originating turn was still in flight when our wait-for-terminal poll timed out.
    /// Retrying just re-enters the same wait, so the chat panel surfaces both Retry AND
    /// Cancel — the latter clears the captured idempotency key so the user can start fresh.
    /// </summary>
    public const string TurnStillRunning = "turn-still-running";

    /// <summary>
    /// AR-1 slow-subscriber drop — a live-tail retry's bounded buffer overflowed. The
    /// originating turn is unaffected; the retry should request a fresh turn (or re-attach,
    /// which will see the post-flush snapshot via Replay).
    /// </summary>
    public const string LiveTailFellBehind = "live-tail-fell-behind";

    /// <summary>
    /// AR-1 lifetime ceiling — the live-tail subscriber's bounded lifetime expired before
    /// the producer flushed (likely the producer crashed or the lifetime is misconfigured).
    /// </summary>
    public const string LiveTailTimeout = "live-tail-timeout";
}
