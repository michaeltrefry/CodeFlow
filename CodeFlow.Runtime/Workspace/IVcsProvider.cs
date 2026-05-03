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

    /// <summary>
    /// Returns an HTTPS clone URL with the platform-managed access token embedded as basic-auth
    /// userinfo, so that <c>git clone</c> can authenticate without the agent ever seeing the
    /// token. Callers MUST scrub the remote URL after the initial fetch (<c>git remote set-url
    /// origin &lt;clean-url&gt;</c>) so the token doesn't persist in <c>.git/config</c>.
    /// </summary>
    /// <param name="repoUrl">Repository URL the agent passed to <c>vcs.clone</c>. The provider
    /// preserves the host/path and only injects the userinfo segment.</param>
    string BuildAuthenticatedCloneUrl(string repoUrl);
}
