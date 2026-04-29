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
