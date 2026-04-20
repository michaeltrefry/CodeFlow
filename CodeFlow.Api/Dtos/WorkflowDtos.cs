using CodeFlow.Runtime;
using System.Text.Json;

namespace CodeFlow.Api.Dtos;

public sealed record WorkflowSummaryDto(
    string Key,
    int LatestVersion,
    string Name,
    string StartAgentKey,
    string? EscalationAgentKey,
    int EdgeCount,
    DateTime CreatedAtUtc);

public sealed record WorkflowEdgeDto(
    string FromAgentKey,
    AgentDecisionKind Decision,
    JsonElement? Discriminator,
    string ToAgentKey,
    bool RotatesRound,
    int SortOrder);

public sealed record WorkflowDetailDto(
    string Key,
    int Version,
    string Name,
    string StartAgentKey,
    string? EscalationAgentKey,
    int MaxRoundsPerRound,
    DateTime CreatedAtUtc,
    IReadOnlyList<WorkflowEdgeDto> Edges);

public sealed record CreateWorkflowRequest(
    string? Key,
    string? Name,
    string? StartAgentKey,
    string? EscalationAgentKey,
    int? MaxRoundsPerRound,
    IReadOnlyList<WorkflowEdgeDto>? Edges);

public sealed record UpdateWorkflowRequest(
    string? Name,
    string? StartAgentKey,
    string? EscalationAgentKey,
    int? MaxRoundsPerRound,
    IReadOnlyList<WorkflowEdgeDto>? Edges);
