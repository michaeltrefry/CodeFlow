using CodeFlow.Runtime;

namespace CodeFlow.Persistence;

public interface IRoleResolutionService
{
    /// <summary>
    /// Resolves the host/MCP/skill grants for the given (agent_key, agent_version) pair.
    /// The runtime invocation path passes the version pinned in the invocation message so
    /// replays of historical traces see the assignment that was in force at the time the
    /// trace was recorded, not whatever is current.
    /// </summary>
    Task<ResolvedAgentTools> ResolveAsync(
        string agentKey,
        int agentVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a single role's tool grants directly by id, bypassing agent-key assignments. Used
    /// by the homepage assistant to expose an admin-selected role's tools without the role having
    /// to be assigned to a synthetic agent. Archived roles return <see cref="ResolvedAgentTools.Empty"/>.
    /// </summary>
    Task<ResolvedAgentTools> ResolveByRoleAsync(long roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Epic 993 / NO-6: resolves a bare list of tool identifiers — host tool names and
    /// <c>mcp:&lt;server&gt;:&lt;tool&gt;</c> grants — into a <see cref="ResolvedAgentTools"/>,
    /// running each through the same host/MCP catalog resolution and ghost-tool warn-and-skip
    /// path used for role grants. The workflow-node-override path uses this to materialise the
    /// per-node additive tools before unioning them into the role-resolved set. An empty input
    /// (or one containing only unknown identifiers) yields <see cref="ResolvedAgentTools.Empty"/>.
    /// </summary>
    Task<ResolvedAgentTools> ResolveToolIdentifiersAsync(
        IEnumerable<string> toolIdentifiers,
        CancellationToken cancellationToken = default);
}
