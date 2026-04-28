namespace CodeFlow.Runtime;

public interface IInvocationObserver
{
    Task OnModelCallStartedAsync(Guid invocationId, int roundNumber, CancellationToken cancellationToken);

    Task OnModelCallCompletedAsync(
        Guid invocationId,
        int roundNumber,
        ChatMessage responseMessage,
        TokenUsage? callTokenUsage,
        TokenUsage? cumulativeTokenUsage,
        CancellationToken cancellationToken);

    Task OnToolCallStartedAsync(ToolCall call, CancellationToken cancellationToken);

    Task OnToolCallCompletedAsync(ToolCall call, ToolResult result, CancellationToken cancellationToken);
}
