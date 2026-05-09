namespace CodeFlow.Api.WorkflowPackages;

public interface IAgentPackageResolver
{
    Task<AgentPackage> ResolveAsync(
        string agentKey,
        int agentVersion,
        CancellationToken cancellationToken = default);
}
