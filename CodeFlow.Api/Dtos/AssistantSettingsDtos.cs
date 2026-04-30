namespace CodeFlow.Api.Dtos;

/// <summary>
/// HAA-15 — DB-backed admin defaults for the homepage AI assistant. Returned by
/// <c>GET /api/admin/assistant-settings</c> and round-tripped on
/// <c>PUT /api/admin/assistant-settings</c>. Provider/model select which configured LLM provider
/// the assistant uses by default; per-conversation cap bounds cumulative tokens.
/// </summary>
public sealed record AssistantSettingsResponse(
    string? Provider,
    string? Model,
    long? MaxTokensPerConversation,
    long? AssignedAgentRoleId,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record AssistantSettingsWriteRequest(
    string? Provider,
    string? Model,
    long? MaxTokensPerConversation,
    long? AssignedAgentRoleId);

/// <summary>
/// sc-274 phase 2 — ambiguity preflight refusal payload for the assistant chat endpoint.
/// Returned with HTTP 422 from <c>POST /api/assistant/conversations/{id}/messages</c> when
/// the user message does not meet the assistant-chat clarity threshold; the chat panel
/// renders the clarification questions inline so the user can refine and re-send without
/// any tokens being spent. Mirrors <see cref="PreflightRefusalResponse"/>'s shape but
/// carries <c>ConversationId</c> instead of <c>OriginalTraceId</c>.
/// </summary>
public sealed record AssistantPreflightRefusalResponse(
    Guid ConversationId,
    string Code,
    string Mode,
    double OverallScore,
    double Threshold,
    IReadOnlyList<PreflightDimensionDto> Dimensions,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> ClarificationQuestions);
