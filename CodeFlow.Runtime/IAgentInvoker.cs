namespace CodeFlow.Runtime;

public interface IAgentInvoker
{
    Task<AgentInvocationResult> InvokeAsync(
        AgentInvocationConfiguration configuration,
        string? input,
        CancellationToken cancellationToken = default);
}
