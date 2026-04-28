namespace CodeFlow.Orchestration.Replay;

/// <summary>
/// Identifies one recorded agent decision after the replay extractor has placed it into a per-agent
/// queue. The pair <c>(AgentKey, OrdinalPerAgent)</c> is what the replay request body's
/// <c>edits[]</c> addresses; the surrounding fields let the UI label rows on the trace timeline
/// without re-fetching the saga.
/// </summary>
public sealed record RecordedDecisionRef(
    string AgentKey,
    int OrdinalPerAgent,
    Guid SagaCorrelationId,
    int SagaOrdinal,
    Guid? NodeId,
    Guid RoundId,
    string OriginalDecision);
