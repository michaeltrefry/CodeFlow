namespace CodeFlow.Persistence;

public interface IAgentRoleRepository
{
    Task<IReadOnlyList<AgentRole>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default);

    Task<AgentRole?> GetAsync(long id, CancellationToken cancellationToken = default);

    Task<AgentRole?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(AgentRoleCreate create, CancellationToken cancellationToken = default);

    Task UpdateAsync(long id, AgentRoleUpdate update, CancellationToken cancellationToken = default);

    Task ArchiveAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentRoleToolGrant>> GetGrantsAsync(long id, CancellationToken cancellationToken = default);

    Task ReplaceGrantsAsync(long id, IReadOnlyList<AgentRoleToolGrant> grants, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentRole>> GetRolesForAgentAsync(string agentKey, CancellationToken cancellationToken = default);

    Task ReplaceAssignmentsAsync(string agentKey, IReadOnlyList<long> roleIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<long>> GetSkillGrantsAsync(long id, CancellationToken cancellationToken = default);

    Task ReplaceSkillGrantsAsync(long id, IReadOnlyList<long> skillIds, CancellationToken cancellationToken = default);
}
