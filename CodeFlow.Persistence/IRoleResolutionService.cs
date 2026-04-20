using CodeFlow.Runtime;

namespace CodeFlow.Persistence;

public interface IRoleResolutionService
{
    Task<ResolvedAgentTools> ResolveAsync(string agentKey, CancellationToken cancellationToken = default);
}
