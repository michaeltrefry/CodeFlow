using System.Text.Json;
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
    DateTime CreatedAtUtc,
    bool IsRetired);

/// <summary>
/// HAA-14 — Workflow summary annotated with the most recent saga activity timestamp. Used by
/// <c>GET /api/workflows/recent</c> so the homepage rail can label "last used" alongside the
/// regular summary fields.
/// </summary>
public sealed record RecentWorkflowDto(
    WorkflowSummaryDto Summary,
    DateTime LastUsedAtUtc);

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
    IReadOnlyDictionary<string, string>? OutputPortReplacements = null,
    string? Template = null,
    string OutputType = "string",
    string? SwarmProtocol = null,
    int? SwarmN = null,
    string? ContributorAgentKey = null,
    int? ContributorAgentVersion = null,
    string? SynthesizerAgentKey = null,
    int? SynthesizerAgentVersion = null,
    string? CoordinatorAgentKey = null,
    int? CoordinatorAgentVersion = null,
    int? SwarmTokenBudget = null);

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
    bool IsRetired,
    IReadOnlyList<WorkflowNodeDto> Nodes,
    IReadOnlyList<WorkflowEdgeDto> Edges,
    IReadOnlyList<WorkflowInputDto> Inputs,
    IReadOnlyList<string>? WorkflowVarsReads = null,
    IReadOnlyList<string>? WorkflowVarsWrites = null);

public sealed record CreateWorkflowRequest(
    string? Key,
    string? Name,
    int? MaxRoundsPerRound,
    WorkflowCategory? Category,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<WorkflowNodeDto>? Nodes,
    IReadOnlyList<WorkflowEdgeDto>? Edges,
    IReadOnlyList<WorkflowInputDto>? Inputs,
    IReadOnlyList<string>? WorkflowVarsReads = null,
    IReadOnlyList<string>? WorkflowVarsWrites = null);

public sealed record UpdateWorkflowRequest(
    string? Name,
    int? MaxRoundsPerRound,
    WorkflowCategory? Category,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<WorkflowNodeDto>? Nodes,
    IReadOnlyList<WorkflowEdgeDto>? Edges,
    IReadOnlyList<WorkflowInputDto>? Inputs,
    IReadOnlyList<string>? WorkflowVarsReads = null,
    IReadOnlyList<string>? WorkflowVarsWrites = null);

public sealed record ValidateScriptRequest(
    string? Script,
    ValidateScriptDirection? Direction = null,
    // Optional. When supplied (and Direction is Output), the validator surfaces
    // setNodePath/setOutput calls that target a port not in this list as an UnknownPort
    // error. When null/empty, port mismatches are silently allowed — useful from the
    // script editor before the script is wired to a node with declared ports.
    IReadOnlyList<string>? DeclaredPorts = null);

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
    IReadOnlyList<WorkflowInputDto>? Inputs,
    IReadOnlyList<string>? WorkflowVarsReads = null,
    IReadOnlyList<string>? WorkflowVarsWrites = null);

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

/// <summary>
/// Request body for the live Transform-node template preview endpoint (TN-6). Mirrors the saga's
/// Transform render scope: <c>input.*</c> (the structured upstream artifact), plus the same
/// <c>context.*</c> / <c>workflow.*</c> dotted-key surfaces the runtime exposes. <c>OutputType</c>
/// matches the persisted node field — when <c>"json"</c>, the rendered output is JSON-parsed and
/// the parsed payload is returned alongside the raw rendered text so the UI can render structure
/// or surface a parse error. When <c>"string"</c> (or null/missing, which defaults to string),
/// only the raw render is returned.
/// </summary>
public sealed record TransformPreviewRequest(
    string? Template,
    string? OutputType,
    JsonElement? Input,
    IReadOnlyDictionary<string, JsonElement>? Context,
    IReadOnlyDictionary<string, JsonElement>? Workflow);

public sealed record TransformPreviewResponse(
    string Rendered,
    JsonElement? Parsed,
    string? JsonParseError);

public sealed record TransformPreviewErrorResponse(string Error);
