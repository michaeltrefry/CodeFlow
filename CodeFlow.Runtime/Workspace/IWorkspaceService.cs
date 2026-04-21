namespace CodeFlow.Runtime.Workspace;

public interface IWorkspaceService
{
    Task<Workspace> OpenAsync(
        Guid correlationId,
        string repoUrl,
        string? baseBranch = null,
        CancellationToken cancellationToken = default);

    Workspace? Get(Guid correlationId, string repoSlug);

    Task ReleaseAsync(Guid correlationId, CancellationToken cancellationToken = default);
}
