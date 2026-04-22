namespace CodeFlow.Runtime.Workspace;

public sealed class Workspace
{
    internal Workspace(
        Guid correlationId,
        RepoReference repo,
        string repoUrl,
        string rootPath,
        string defaultBranch,
        string currentBranch,
        string mirrorPath)
    {
        CorrelationId = correlationId;
        Repo = repo;
        RepoUrl = repoUrl;
        RootPath = rootPath;
        DefaultBranch = defaultBranch;
        CurrentBranch = currentBranch;
        MirrorPath = mirrorPath;
    }

    public Guid CorrelationId { get; }

    public RepoReference Repo { get; }

    public string RepoSlug => Repo.Slug;

    public string RepoIdentityKey => Repo.IdentityKey;

    public string RepoUrl { get; }

    public string RootPath { get; }

    public string DefaultBranch { get; }

    public string CurrentBranch { get; internal set; }

    internal string MirrorPath { get; }
}
