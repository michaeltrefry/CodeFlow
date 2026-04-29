namespace CodeFlow.Api.Dtos;

/// <summary>
/// HAA-14 — Aggregated token usage across the caller's assistant conversations. Drives the
/// homepage rail's assistant-token chip and any future "Assistant" stream view in the token
/// panel. Assistant traces are synthetic (one <c>SyntheticTraceId</c> per conversation) and
/// don't appear in the workflow trace list, so this endpoint is the only path for the UI to
/// see assistant token usage rolled up across the user's threads.
/// </summary>
/// <param name="Today">Rollup over records recorded since calendar UTC start of today. Used
/// for the rail's "today" chip.</param>
/// <param name="AllTime">Rollup over every captured assistant token record for the user.</param>
/// <param name="PerConversation">Per-conversation rollup, in the same order as
/// <see cref="IAssistantConversationRepository.ListByUserAsync"/>. Conversations with zero
/// captured records are filtered out so the rail doesn't render empty rows.</param>
public sealed record AssistantTokenUsageSummaryDto(
    TokenUsageRollupDto Today,
    TokenUsageRollupDto AllTime,
    IReadOnlyList<AssistantConversationTokenUsageDto> PerConversation);

public sealed record AssistantConversationTokenUsageDto(
    Guid ConversationId,
    Guid SyntheticTraceId,
    ScopeDto Scope,
    TokenUsageRollupDto Rollup);

/// <summary>
/// HAA-14 — Standardized scope shape on the assistant API surface. The existing
/// <c>POST /conversations</c> response uses anonymous-typed scope objects with the same shape;
/// new endpoints use this typed record so JSON test deserialization can target it.
/// </summary>
public sealed record ScopeDto(string Kind, string? EntityType, string? EntityId);
