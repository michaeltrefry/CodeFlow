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
    bool Force,
    /// <summary>
    /// sc-275 — caller-supplied source identifier persisted on the replay attempt row
    /// (e.g. <c>ui:replay-panel</c>, <c>assistant:propose_replay_with_edit</c>).
    /// Optional; not used for content hashing.
    /// </summary>
    string? Reason = null);

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
    ReplayDriftDto Drift,
    /// <summary>
    /// sc-275 — lineage metadata for the just-recorded replay attempt. Always populated
    /// on a successful replay, even when persistence of the underlying row failed (the
    /// hash + lineage id are computed in-process and don't depend on the DB write).
    /// </summary>
    ReplayLineageDto? Lineage = null);

/// <summary>
/// sc-275 — lineage metadata returned with every successful replay response. Identical
/// inputs against the same parent produce the same <see cref="LineageId"/>, so authors
/// see "you've already tried this exact replay" instead of being confused by drift
/// between otherwise-identical attempts.
/// </summary>
public sealed record ReplayLineageDto(
    Guid LineageId,
    string ContentHash,
    Guid ParentTraceId,
    int Generation,
    DateTime CreatedAtUtc,
    string? Reason);

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

// sc-274 phase 1 — ambiguity preflight refusal payload. Returned with HTTP 422 when the
// replay edits do not meet the mode's clarity threshold; the UI renders the clarification
// questions inline so the author can refine and re-submit without leaving the panel.

public sealed record PreflightRefusalResponse(
    Guid OriginalTraceId,
    string Code,
    string Mode,
    double OverallScore,
    double Threshold,
    IReadOnlyList<PreflightDimensionDto> Dimensions,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> ClarificationQuestions);

public sealed record PreflightDimensionDto(
    string Dimension,
    double Score,
    string? Reason);
