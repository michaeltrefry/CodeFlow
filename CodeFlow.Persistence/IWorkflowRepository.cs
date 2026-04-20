using CodeFlow.Runtime;
using System.Text.Json;

namespace CodeFlow.Persistence;

public interface IWorkflowRepository
{
    Task<Workflow> GetAsync(string key, int version, CancellationToken cancellationToken = default);

    Task<WorkflowEdge?> FindNextAsync(
        string key,
        int version,
        string fromAgentKey,
        AgentDecision decision,
        JsonElement? discriminator = null,
        CancellationToken cancellationToken = default);
}
