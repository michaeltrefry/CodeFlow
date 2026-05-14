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
    IReadOnlyList<string> Tags,
    DateTime LatestCreatedAtUtc,
    string? LatestCreatedBy,
    bool IsRetired);

public sealed record AgentVersionSummaryDto(
    string Key,
    int Version,
    IReadOnlyList<string> Tags,
    DateTime CreatedAtUtc,
    string? CreatedBy);

public sealed record AgentVersionDto(
    string Key,
    int Version,
    string Type,
    JsonNode? Config,
    IReadOnlyList<string> Tags,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    bool IsRetired);

/// <summary>
/// Epic 993 / NO-8: the tool identifiers an agent resolves through its role grants at a
/// given version — host tool names and <c>mcp:&lt;server&gt;:&lt;tool&gt;</c> identifiers. The
/// workflow editor's node-overrides tools picker uses this to render the agent's inherited
/// tools checked + disabled, so the author only adds tools on top.
/// </summary>
public sealed record AgentResolvedToolsDto(
    IReadOnlyList<string> ToolIdentifiers,
    bool EnableHostTools);

public sealed record BulkRetireKeysRequest(IReadOnlyList<string>? Keys);

public sealed record BulkRetireKeysResponse(
    IReadOnlyList<string> RetiredKeys,
    IReadOnlyList<string> MissingKeys);

/// <summary>
/// POST /api/agents body. <see cref="RoleIds"/> (sc-828 / AR-4) is optional — when
/// supplied, the new agent's v1 row lands with the assignment slot atomically, no
/// follow-up bump needed. Supports library seeders + the agent-package importer's
/// create-with-roles paths and keeps the AP-10 round-trip green without a second PUT.
/// </summary>
public sealed record CreateAgentRequest(
    string? Key,
    JsonElement? Config,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<long>? RoleIds = null);

public sealed record UpdateAgentRequest(JsonElement? Config, IReadOnlyList<string>? Tags);

public sealed record ForkAgentRequest(
    string? SourceKey,
    int? SourceVersion,
    string? WorkflowKey,
    JsonElement? Config,
    IReadOnlyList<string>? Tags);

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
    bool? AcknowledgeDrift,
    IReadOnlyList<string>? Tags);

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

public sealed record PromptPartialPinDto(string Key, int Version);

/// <summary>
/// Request body for the live prompt-template preview endpoint (VZ3). Mirrors the runtime invocation
/// scope so the rendered output matches what the model would actually receive: workflow / context /
/// input flatten through the same Scriban dotted-key shape, ReviewLoop bindings (<c>round</c>,
/// <c>maxRounds</c>, <c>isLastRound</c>, <c>rejectionHistory</c>) are surfaced when both
/// <see cref="ReviewRound"/> and <see cref="ReviewMaxRounds"/> are supplied, and partial pins
/// resolve via <see cref="CodeFlow.Persistence.IPromptPartialRepository"/> exactly like runtime.
/// </summary>
public sealed record PromptTemplatePreviewRequest(
    string? SystemPrompt,
    string? PromptTemplate,
    IReadOnlyDictionary<string, JsonElement>? Workflow,
    IReadOnlyDictionary<string, JsonElement>? Context,
    string? Input,
    int? ReviewRound,
    int? ReviewMaxRounds,
    bool? OptOutLastRoundReminder,
    IReadOnlyList<PromptPartialPinDto>? PartialPins);

public sealed record PromptTemplatePreviewAutoInjection(
    string Key,
    string RenderedBody,
    string Reason);

public sealed record PromptTemplatePreviewMissingPartial(string Key, int Version);

public sealed record PromptTemplatePreviewResponse(
    string? RenderedSystemPrompt,
    string? RenderedPromptTemplate,
    IReadOnlyList<PromptTemplatePreviewAutoInjection> AutoInjections,
    IReadOnlyList<PromptTemplatePreviewMissingPartial> MissingPartials);

public sealed record PromptTemplatePreviewErrorResponse(
    string Error,
    IReadOnlyList<PromptTemplatePreviewMissingPartial>? MissingPartials = null);
