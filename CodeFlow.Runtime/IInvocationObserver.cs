namespace CodeFlow.Runtime;

public interface IInvocationObserver
{
    Task OnModelCallStartedAsync(int roundNumber, CancellationToken cancellationToken);

    Task OnModelCallCompletedAsync(
        int roundNumber,
        ChatMessage responseMessage,
        TokenUsage? callTokenUsage,
        TokenUsage? cumulativeTokenUsage,
        CancellationToken cancellationToken);

    Task OnToolCallStartedAsync(ToolCall call, CancellationToken cancellationToken);

    Task OnToolCallCompletedAsync(ToolCall call, ToolResult result, CancellationToken cancellationToken);
}
