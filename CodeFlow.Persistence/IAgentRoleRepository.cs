namespace CodeFlow.Persistence;

public interface IAgentRoleRepository
{
    Task<IReadOnlyList<AgentRole>> ListAsync(
        bool includeArchived,
        CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<AgentRole>> ListAsync(
        bool includeArchived,
        bool includeRetired,
        CancellationToken cancellationToken = default)
    {
        var roles = await ListAsync(includeArchived, cancellationToken);
        return includeRetired
            ? roles
            : roles.Where(role => !role.IsRetired).ToArray();
    }

    Task<AgentRole?> GetAsync(long id, CancellationToken cancellationToken = default);

    Task<AgentRole?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(AgentRoleCreate create, CancellationToken cancellationToken = default);

    Task UpdateAsync(long id, AgentRoleUpdate update, CancellationToken cancellationToken = default);

    Task ArchiveAsync(long id, CancellationToken cancellationToken = default);

    Task RetireAsync(long id, CancellationToken cancellationToken = default)
    {
        throw new AgentRoleNotFoundException(id);
    }

    Task<IReadOnlyList<long>> RetireManyAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
    }

    Task<IReadOnlyList<AgentRoleToolGrant>> GetGrantsAsync(long id, CancellationToken cancellationToken = default);

    Task ReplaceGrantsAsync(long id, IReadOnlyList<AgentRoleToolGrant> grants, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the roles assigned to the given (agent_key, agent_version) tuple. Use this
    /// from the runtime resolution path, validation rules, and package resolvers — anywhere
    /// the caller knows exactly which version of the agent it cares about.
    /// </summary>
    Task<IReadOnlyList<AgentRole>> GetRolesForAgentAsync(string agentKey, int agentVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the roles assigned to the highest <c>agent_version</c> row for the given key.
    /// Use this from admin UI surfaces that default to "the current assignment" without
    /// pinning to a specific version. Returns an empty list when no assignment rows exist.
    /// </summary>
    Task<IReadOnlyList<AgentRole>> GetRolesForAgentLatestAsync(string agentKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the role-assignment slot for the given (agent_key, agent_version). Use this
    /// when the caller knows exactly which version to write at — package importers writing
    /// per imported agent, validation rules seeding fixtures at a known version, etc. Bump-
    /// on-write semantics for admin edits live on
    /// <see cref="BumpAgentForRoleAssignmentChangeAsync"/> instead.
    /// </summary>
    Task ReplaceAssignmentsAsync(
        string agentKey,
        int agentVersion,
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the role-assignment slot for the latest agent version. Use only from paths
    /// that explicitly want "edit the current version's assignment in place" without
    /// bumping (template materializers, tests). Falls back to <c>agent_version = 0</c> for
    /// orphan keys with no <c>agents</c> row.
    /// </summary>
    Task ReplaceAssignmentsForLatestAsync(
        string agentKey,
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// sc-828 / AR-4: bump the agent to a new version with the same body and the supplied
    /// role assignment. Returns the new version. Throws
    /// <see cref="AgentConfigVersionDriftException"/> when <paramref name="expectedFromVersion"/>
    /// is supplied and disagrees with the actual latest version (admin UI surfaces this as a
    /// 409 + refresh affordance).
    /// </summary>
    Task<int> BumpAgentForRoleAssignmentChangeAsync(
        string agentKey,
        IReadOnlyList<long> roleIds,
        int? expectedFromVersion,
        string? createdBy,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<long>> GetSkillGrantsAsync(long id, CancellationToken cancellationToken = default);

    Task ReplaceSkillGrantsAsync(long id, IReadOnlyList<long> skillIds, CancellationToken cancellationToken = default);
}
