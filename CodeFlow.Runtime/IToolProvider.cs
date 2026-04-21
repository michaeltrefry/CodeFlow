namespace CodeFlow.Runtime;

public interface IToolProvider
{
    ToolCategory Category { get; }

    IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy policy);

    Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        AgentInvocationContext context,
        CancellationToken cancellationToken = default);
}
