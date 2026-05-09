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

    Task ReplaceAssignmentsAsync(string agentKey, IReadOnlyList<long> roleIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<long>> GetSkillGrantsAsync(long id, CancellationToken cancellationToken = default);

    Task ReplaceSkillGrantsAsync(long id, IReadOnlyList<long> skillIds, CancellationToken cancellationToken = default);
}
