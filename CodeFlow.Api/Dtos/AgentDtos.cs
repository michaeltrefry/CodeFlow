using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.Dtos;

public sealed record AgentSummaryDto(
    string Key,
    int LatestVersion,
    string? Name,
    string? Provider,
    string? Model,
    string Type,
    DateTime LatestCreatedAtUtc,
    string? LatestCreatedBy,
    bool IsRetired);

public sealed record AgentVersionSummaryDto(
    string Key,
    int Version,
    DateTime CreatedAtUtc,
    string? CreatedBy);

public sealed record AgentVersionDto(
    string Key,
    int Version,
    string Type,
    JsonNode? Config,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    bool IsRetired);

public sealed record CreateAgentRequest(string? Key, JsonElement? Config);

public sealed record UpdateAgentRequest(JsonElement? Config);

public sealed record ForkAgentRequest(
    string? SourceKey,
    int? SourceVersion,
    string? WorkflowKey,
    JsonElement? Config);

public sealed record ForkAgentResponse(
    string Key,
    int Version,
    string ForkedFromKey,
    int ForkedFromVersion,
    string OwningWorkflowKey);

public sealed record PublishForkStatusResponse(
    string ForkedFromKey,
    int ForkedFromVersion,
    int? OriginalLatestVersion,
    bool IsDrift);

public sealed record PublishForkRequest(
    string? Mode,
    string? NewKey,
    bool? AcknowledgeDrift);

public sealed record PublishForkResponse(
    string PublishedKey,
    int PublishedVersion,
    string ForkedFromKey,
    int ForkedFromVersion);

public enum DecisionOutputTemplateMode
{
    Llm,
    Hitl
}

public sealed record DecisionOutputTemplatePreviewRequest(
    string? Template,
    DecisionOutputTemplateMode? Mode,
    string? Decision,
    string? OutputPortName,
    string? Output,
    JsonElement? Input,
    IReadOnlyDictionary<string, JsonElement>? FieldValues,
    IReadOnlyDictionary<string, JsonElement>? Context,
    IReadOnlyDictionary<string, JsonElement>? Workflow,
    string? Reason,
    IReadOnlyList<string>? Reasons,
    IReadOnlyList<string>? Actions);

public sealed record DecisionOutputTemplatePreviewResponse(string Rendered);

public sealed record DecisionOutputTemplatePreviewErrorResponse(string Error);
