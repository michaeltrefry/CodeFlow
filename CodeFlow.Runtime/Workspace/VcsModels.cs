namespace CodeFlow.Runtime.Workspace;

public enum VcsRepoVisibility
{
    Unknown = 0,
    Public,
    Private,
    Internal,
}

public sealed record VcsRepoMetadata(
    string DefaultBranch,
    string CloneUrl,
    VcsRepoVisibility Visibility);

public sealed record PullRequestInfo(string Url, long Number);
