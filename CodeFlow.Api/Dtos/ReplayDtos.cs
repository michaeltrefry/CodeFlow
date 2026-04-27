using System.Text.Json.Nodes;

namespace CodeFlow.Api.Dtos;

// ---------- Replay-with-edit ----------

/// <summary>
/// Request body for <c>POST /api/traces/{id}/replay</c>. All fields are optional; the empty body
/// replays the trace with no edits, which is the round-trip identity case.
/// </summary>
public sealed record ReplayRequest(
    IReadOnlyList<ReplayEditDto>? Edits,
    IReadOnlyDictionary<string, IReadOnlyList<ReplayMockResponseDto>>? AdditionalMocks,
    int? WorkflowVersionOverride,
    bool Force);

public sealed record ReplayEditDto(
    string AgentKey,
    int Ordinal,
    string? Decision,
    string? Output,
    JsonNode? Payload);

public sealed record ReplayMockResponseDto(
    string Decision,
    string? Output,
    JsonNode? Payload);

public sealed record ReplayResponse(
    Guid OriginalTraceId,
    string ReplayState,
    string? ReplayTerminalPort,
    string? FailureReason,
    string? FailureCode,
    ReplayExhaustedAgentDto? ExhaustedAgent,
    IReadOnlyList<RecordedDecisionRefDto> Decisions,
    IReadOnlyList<DryRunEventDto> ReplayEvents,
    DryRunHitlPayloadDto? HitlPayload,
    ReplayDriftDto Drift);

public sealed record ReplayDriftDto(
    string Level,
    IReadOnlyList<string> Warnings);

public sealed record ReplayExhaustedAgentDto(
    string AgentKey,
    int RecordedResponses);

public sealed record RecordedDecisionRefDto(
    string AgentKey,
    int OrdinalPerAgent,
    Guid SagaCorrelationId,
    int SagaOrdinal,
    Guid? NodeId,
    Guid RoundId,
    string OriginalDecision);
