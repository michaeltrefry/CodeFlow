using System.Text.Json;

namespace CodeFlow.Runtime;

public interface IInvocationObserver
{
    Task OnModelCallStartedAsync(Guid invocationId, int roundNumber, CancellationToken cancellationToken);

    /// <param name="provider">Free-form provider label resolved from
    /// <c>AgentInvocationConfiguration.Provider</c> (e.g., "openai", "anthropic", "lmstudio").</param>
    /// <param name="model">Free-form model identifier from <c>InvocationRequest.Model</c>.</param>
    /// <param name="rawUsage">The provider's <c>usage</c> object cloned verbatim by the model
    /// client. Null when the model client does not surface raw usage. The token-usage capture
    /// observer persists a <c>TokenUsageRecord</c> only when this is non-null — flat
    /// <see cref="TokenUsage"/> alone would lose cache_creation/cache_read/reasoning fields.</param>
    Task OnModelCallCompletedAsync(
        Guid invocationId,
        int roundNumber,
        ChatMessage responseMessage,
        TokenUsage? callTokenUsage,
        TokenUsage? cumulativeTokenUsage,
        string provider,
        string model,
        JsonElement? rawUsage,
        CancellationToken cancellationToken);

    Task OnToolCallStartedAsync(ToolCall call, CancellationToken cancellationToken);

    Task OnToolCallCompletedAsync(ToolCall call, ToolResult result, CancellationToken cancellationToken);
}
