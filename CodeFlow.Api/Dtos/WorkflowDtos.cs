using CodeFlow.Persistence;

namespace CodeFlow.Api.Dtos;

public sealed record WorkflowSummaryDto(
    string Key,
    int LatestVersion,
    string Name,
    WorkflowCategory Category,
    IReadOnlyList<string> Tags,
    int NodeCount,
    int EdgeCount,
    int InputCount,
    DateTime CreatedAtUtc);

public sealed record WorkflowNodeDto(
    Guid Id,
    WorkflowNodeKind Kind,
    string? AgentKey,
    int? AgentVersion,
    string? OutputScript,
    IReadOnlyList<string> OutputPorts,
    double LayoutX,
    double LayoutY,
    string? SubflowKey = null,
    int? SubflowVersion = null,
    int? ReviewMaxRounds = null,
    string? LoopDecision = null,
    string? InputScript = null,
    bool OptOutLastRoundReminder = false,
    RejectionHistoryConfig? RejectionHistory = null,
    string? MirrorOutputToWorkflowVar = null,
    IReadOnlyDictionary<string, string>? OutputPortReplacements = null);

public sealed record WorkflowEdgeDto(
    Guid FromNodeId,
    string FromPort,
    Guid ToNodeId,
    string ToPort,
    bool RotatesRound,
    int SortOrder,
    bool IntentionalBackedge = false);

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
    WorkflowCategory Category,
    IReadOnlyList<string> Tags,
    DateTime CreatedAtUtc,
    IReadOnlyList<WorkflowNodeDto> Nodes,
    IReadOnlyList<WorkflowEdgeDto> Edges,
    IReadOnlyList<WorkflowInputDto> Inputs);

public sealed record CreateWorkflowRequest(
    string? Key,
    string? Name,
    int? MaxRoundsPerRound,
    WorkflowCategory? Category,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<WorkflowNodeDto>? Nodes,
    IReadOnlyList<WorkflowEdgeDto>? Edges,
    IReadOnlyList<WorkflowInputDto>? Inputs);

public sealed record UpdateWorkflowRequest(
    string? Name,
    int? MaxRoundsPerRound,
    WorkflowCategory? Category,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<WorkflowNodeDto>? Nodes,
    IReadOnlyList<WorkflowEdgeDto>? Edges,
    IReadOnlyList<WorkflowInputDto>? Inputs);

public sealed record ValidateScriptRequest(
    string? Script,
    ValidateScriptDirection? Direction = null);

public enum ValidateScriptDirection
{
    Input,
    Output
}

public sealed record ValidateScriptResponse(bool Ok, IReadOnlyList<ValidateScriptError> Errors);

public sealed record ValidateScriptError(int Line, int Column, string Message);

public sealed record ValidateWorkflowRequest(
    string? Key,
    string? Name,
    int? MaxRoundsPerRound,
    IReadOnlyList<WorkflowNodeDto>? Nodes,
    IReadOnlyList<WorkflowEdgeDto>? Edges,
    IReadOnlyList<WorkflowInputDto>? Inputs);

public sealed record WorkflowValidationLocationDto(
    Guid? NodeId,
    Guid? EdgeFrom,
    string? EdgePort);

public sealed record WorkflowValidationFindingDto(
    string RuleId,
    string Severity,
    string Message,
    WorkflowValidationLocationDto? Location);

public sealed record ValidateWorkflowResponse(
    bool HasErrors,
    bool HasWarnings,
    IReadOnlyList<WorkflowValidationFindingDto> Findings);
