namespace CodeFlow.Runtime;

public interface IAgentInvoker
{
    Task<AgentInvocationResult> InvokeAsync(
        AgentInvocationConfiguration configuration,
        AgentInvocationContext context,
        string? input,
        ResolvedAgentTools tools,
        CancellationToken cancellationToken = default);
}
