namespace CodeFlow.Runtime.Workspace;

public interface IGitHostSettingsReader
{
    Task<GitHostSettings?> GetAsync(CancellationToken cancellationToken = default);
}
