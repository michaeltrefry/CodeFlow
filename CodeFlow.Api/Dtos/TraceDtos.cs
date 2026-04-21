using CodeFlow.Runtime;
using System.Text.Json;

namespace CodeFlow.Api.Dtos;

public sealed record CreateTraceRequest(
    string? WorkflowKey,
    int? WorkflowVersion,
    string? Input,
    string? InputFileName,
    IReadOnlyDictionary<string, JsonElement>? Inputs);

public sealed record CreateTraceResponse(Guid TraceId);

public sealed record TraceSummaryDto(
    Guid TraceId,
    string WorkflowKey,
    int WorkflowVersion,
    string CurrentState,
    string CurrentAgentKey,
    int RoundCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record TraceDecisionDto(
    string AgentKey,
    int AgentVersion,
    AgentDecisionKind Decision,
    JsonElement? DecisionPayload,
    Guid RoundId,
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
    IReadOnlyList<TraceDecisionDto> Decisions,
    IReadOnlyList<HitlTaskDto> PendingHitl,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

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
    AgentDecisionKind? Decision,
    DateTime? DecidedAtUtc,
    string? DeciderId);

public sealed record HitlDecisionRequest(
    AgentDecisionKind Decision,
    string? Reason,
    IReadOnlyList<string>? Actions,
    IReadOnlyList<string>? Reasons,
    string? OutputText);
