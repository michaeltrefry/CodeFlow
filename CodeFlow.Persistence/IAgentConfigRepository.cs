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

    Task<AgentConfig> CreateForkAsync(
        string sourceKey,
        int sourceVersion,
        string workflowKey,
        string configJson,
        string? createdBy,
        CancellationToken cancellationToken = default);

    Task<int> CreatePublishedVersionAsync(
        string targetKey,
        string configJson,
        string forkedFromKey,
        int forkedFromVersion,
        string? createdBy,
        CancellationToken cancellationToken = default);
}
