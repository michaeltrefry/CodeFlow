using CodeFlow.Runtime;

namespace CodeFlow.Persistence;

public interface IRoleResolutionService
{
    Task<ResolvedAgentTools> ResolveAsync(string agentKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a single role's tool grants directly by id, bypassing agent-key assignments. Used
    /// by the homepage assistant to expose an admin-selected role's tools without the role having
    /// to be assigned to a synthetic agent. Archived roles return <see cref="ResolvedAgentTools.Empty"/>.
    /// </summary>
    Task<ResolvedAgentTools> ResolveByRoleAsync(long roleId, CancellationToken cancellationToken = default);
}
