using CodeFlow.Persistence;

namespace CodeFlow.Api.Dtos;

public sealed record WorkflowSummaryDto(
    string Key,
    int LatestVersion,
    string Name,
    int NodeCount,
    int EdgeCount,
    int InputCount,
    DateTime CreatedAtUtc);

public sealed record WorkflowNodeDto(
    Guid Id,
    WorkflowNodeKind Kind,
    string? AgentKey,
    int? AgentVersion,
    string? Script,
    IReadOnlyList<string> OutputPorts,
    double LayoutX,
    double LayoutY,
    string? SubflowKey = null,
    int? SubflowVersion = null);

public sealed record WorkflowEdgeDto(
    Guid FromNodeId,
    string FromPort,
    Guid ToNodeId,
    string ToPort,
    bool RotatesRound,
    int SortOrder);

public sealed record WorkflowInputDto(
    string Key,
    string DisplayName,
    WorkflowInputKind Kind,
    bool Required,
    string? DefaultValueJson,
    string? Description,
    int Ordinal);

public sealed record WorkflowDetailDto(
    string Key,
    int Version,
    string Name,
    int MaxRoundsPerRound,
    DateTime CreatedAtUtc,
    IReadOnlyList<WorkflowNodeDto> Nodes,
    IReadOnlyList<WorkflowEdgeDto> Edges,
    IReadOnlyList<WorkflowInputDto> Inputs);

public sealed record CreateWorkflowRequest(
    string? Key,
    string? Name,
    int? MaxRoundsPerRound,
    IReadOnlyList<WorkflowNodeDto>? Nodes,
    IReadOnlyList<WorkflowEdgeDto>? Edges,
    IReadOnlyList<WorkflowInputDto>? Inputs);

public sealed record UpdateWorkflowRequest(
    string? Name,
    int? MaxRoundsPerRound,
    IReadOnlyList<WorkflowNodeDto>? Nodes,
    IReadOnlyList<WorkflowEdgeDto>? Edges,
    IReadOnlyList<WorkflowInputDto>? Inputs);

public sealed record ValidateScriptRequest(string? Script);

public sealed record ValidateScriptResponse(bool Ok, IReadOnlyList<ValidateScriptError> Errors);

public sealed record ValidateScriptError(int Line, int Column, string Message);
