namespace CodeFlow.Persistence;

public interface IAgentConfigRepository
{
    /// <summary>
    /// Get a specific agent-config version. Throws <see cref="AgentConfigNotFoundException"/>
    /// when absent — use this overload only when the absence is genuinely an exceptional condition.
    /// For "absence is expected and the caller wants to react to it" lookups, prefer
    /// <see cref="TryGetAsync"/> (F-015 in the 2026-04-28 backend review).
    /// </summary>
    Task<AgentConfig> GetAsync(string key, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a specific agent-config version, returning <c>null</c> when absent. Avoids the
    /// exception-as-flow cost on the request hot path for endpoints that treat "not found"
    /// as an ordinary 404 / validation outcome. The default implementation falls back to the
    /// exception-throwing <see cref="GetAsync"/> so existing test fakes don't have to be
    /// updated; production implementations override it for the perf win.
    /// </summary>
    async Task<AgentConfig?> TryGetAsync(string key, int version, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync(key, version, cancellationToken);
        }
        catch (AgentConfigNotFoundException)
        {
            return null;
        }
    }

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
