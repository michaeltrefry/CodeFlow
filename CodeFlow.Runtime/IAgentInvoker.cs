namespace CodeFlow.Runtime;

public interface IAgentInvoker
{
    Task<AgentInvocationResult> InvokeAsync(
        AgentInvocationConfiguration configuration,
        string? input,
        ResolvedAgentTools tools,
        CancellationToken cancellationToken = default,
        ToolExecutionContext? toolExecutionContext = null);
}
