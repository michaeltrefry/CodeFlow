namespace CodeFlow.Runtime.Workspace;

public interface IVcsProvider
{
    GitHostMode Mode { get; }

    Task<VcsRepoMetadata> GetRepoMetadataAsync(
        string owner,
        string name,
        CancellationToken cancellationToken = default);

    Task<PullRequestInfo> OpenPullRequestAsync(
        string owner,
        string name,
        string head,
        string baseRef,
        string title,
        string body,
        CancellationToken cancellationToken = default);
}
