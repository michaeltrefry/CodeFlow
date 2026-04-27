using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodeFlow.Api.Dtos;

// ---------- WorkflowFixture CRUD ----------

public sealed record WorkflowFixtureSummaryResponse(
    long Id,
    string WorkflowKey,
    string FixtureKey,
    string DisplayName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record WorkflowFixtureDetailResponse(
    long Id,
    string WorkflowKey,
    string FixtureKey,
    string DisplayName,
    string? StartingInput,
    JsonNode MockResponses,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record WorkflowFixtureCreateRequest(
    string WorkflowKey,
    string FixtureKey,
    string DisplayName,
    string? StartingInput,
    JsonNode? MockResponses);

public sealed record WorkflowFixtureUpdateRequest(
    string FixtureKey,
    string DisplayName,
    string? StartingInput,
    JsonNode? MockResponses);

// ---------- Dry-run execution ----------

public sealed record DryRunRequestBody(
    long? FixtureId,
    int? WorkflowVersion,
    string? StartingInput,
    JsonNode? MockResponses);

public sealed record DryRunResponse(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("terminalPort")] string? TerminalPort,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("finalArtifact")] string? FinalArtifact,
    [property: JsonPropertyName("hitlPayload")] DryRunHitlPayloadDto? HitlPayload,
    [property: JsonPropertyName("workflowVariables")] IReadOnlyDictionary<string, JsonElement> WorkflowVariables,
    [property: JsonPropertyName("contextVariables")] IReadOnlyDictionary<string, JsonElement> ContextVariables,
    [property: JsonPropertyName("events")] IReadOnlyList<DryRunEventDto> Events);

public sealed record DryRunHitlPayloadDto(
    Guid NodeId,
    string AgentKey,
    string? Input);

public sealed record DryRunEventDto(
    int Ordinal,
    string Kind,
    Guid NodeId,
    string NodeKind,
    string? AgentKey,
    string? PortName,
    string? Message,
    string? InputPreview,
    string? OutputPreview,
    int? ReviewRound,
    int? MaxRounds,
    int? SubflowDepth,
    string? SubflowKey,
    int? SubflowVersion,
    IReadOnlyList<string>? Logs,
    JsonNode? DecisionPayload);
