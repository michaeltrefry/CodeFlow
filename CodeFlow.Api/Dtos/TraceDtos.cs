using System.Text.Json;

namespace CodeFlow.Api.Dtos;

public sealed record CreateTraceRequest(
    string? WorkflowKey,
    int? WorkflowVersion,
    string? Input,
    string? InputFileName,
    IReadOnlyDictionary<string, JsonElement>? Inputs);

public sealed record CreateTraceResponse(Guid TraceId);

/// <summary>
/// sc-274 phase 3 — ambiguity preflight refusal payload for the workflow launch endpoint.
/// Returned with HTTP 422 from <c>POST /api/traces</c> when the launch <c>Input</c> does not
/// meet the brownfield/greenfield clarity threshold; the trace-submit page renders the
/// clarification questions inline so the launcher can refine and re-submit without ever
/// creating a trace. Mirrors <see cref="PreflightRefusalResponse"/>'s shape but carries
/// <c>WorkflowKey</c> instead of <c>OriginalTraceId</c> — workflow launches refuse before a
/// trace exists.
/// </summary>
public sealed record WorkflowPreflightRefusalResponse(
    string WorkflowKey,
    string Code,
    string Mode,
    double OverallScore,
    double Threshold,
    IReadOnlyList<PreflightDimensionDto> Dimensions,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> ClarificationQuestions);

public sealed record BulkDeleteTracesRequest(
    string? State,
    int OlderThanDays);

public sealed record BulkDeleteTracesResponse(int DeletedCount);

public sealed record TraceSummaryDto(
    Guid TraceId,
    string WorkflowKey,
    int WorkflowVersion,
    string CurrentState,
    string CurrentAgentKey,
    int RoundCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? ParentTraceId = null,
    Guid? ParentNodeId = null,
    int? ParentReviewRound = null,
    int? ParentReviewMaxRounds = null);

public sealed record TraceDescendantDto(
    TraceSummaryDto Summary,
    TraceDetailDto Detail);

public sealed record TraceDecisionDto(
    string AgentKey,
    int AgentVersion,
    string Decision,
    JsonElement? DecisionPayload,
    Guid RoundId,
    DateTime RecordedAtUtc,
    Guid? NodeId,
    string? OutputPortName,
    string? InputRef,
    string? OutputRef,
    DateTime? NodeEnteredAtUtc);

public sealed record TraceLogicEvaluationDto(
    Guid NodeId,
    string? OutputPortName,
    Guid RoundId,
    TimeSpan Duration,
    IReadOnlyList<string> Logs,
    string? FailureKind,
    string? FailureMessage,
    DateTime RecordedAtUtc);

public sealed record TraceDetailDto(
    Guid TraceId,
    string WorkflowKey,
    int WorkflowVersion,
    string CurrentState,
    string CurrentAgentKey,
    Guid CurrentRoundId,
    int RoundCount,
    IReadOnlyDictionary<string, int> PinnedAgentVersions,
    IReadOnlyDictionary<string, JsonElement> ContextInputs,
    IReadOnlyList<TraceDecisionDto> Decisions,
    IReadOnlyList<TraceLogicEvaluationDto> LogicEvaluations,
    IReadOnlyList<HitlTaskDto> PendingHitl,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string? FailureReason);

public sealed record HitlTaskDto(
    long Id,
    Guid TraceId,
    Guid RoundId,
    string AgentKey,
    int AgentVersion,
    Uri InputRef,
    string? InputPreview,
    DateTime CreatedAtUtc,
    string State,
    string? Decision,
    DateTime? DecidedAtUtc,
    string? DeciderId,
    Guid? OriginTraceId = null,
    IReadOnlyList<string>? SubflowPath = null);

public sealed record HitlDecisionRequest(
    string OutputPortName,
    string? Reason,
    IReadOnlyList<string>? Actions,
    IReadOnlyList<string>? Reasons,
    string? OutputText,
    IReadOnlyDictionary<string, JsonElement>? FieldValues = null);
