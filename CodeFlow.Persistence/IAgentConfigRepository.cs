namespace CodeFlow.Persistence;

public interface IAgentConfigRepository
{
    Task<AgentConfig> GetAsync(string key, int version, CancellationToken cancellationToken = default);

    Task<int> CreateNewVersionAsync(
        string key,
        string configJson,
        string? createdBy,
        CancellationToken cancellationToken = default);

    Task<int> GetLatestVersionAsync(string key, CancellationToken cancellationToken = default);

    Task<bool> RetireAsync(string key, CancellationToken cancellationToken = default);
}
