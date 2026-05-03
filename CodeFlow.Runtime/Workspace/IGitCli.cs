namespace CodeFlow.Runtime.Workspace;

public interface IGitCli
{
    Task CloneMirrorAsync(string originUrl, string destinationMirrorPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a working-tree clone of <paramref name="originUrl"/> into <paramref name="destinationPath"/>.
    /// The destination must not already exist as a directory. Optional <paramref name="branch"/> is passed to
    /// <c>git clone --branch</c>; optional <paramref name="depth"/> is passed to <c>git clone --depth</c> for a
    /// shallow clone. Use this for the <c>vcs.clone</c> host tool path; <see cref="CloneMirrorAsync"/> is for the
    /// platform-managed bare-mirror cache.
    /// </summary>
    Task<GitCloneResult> CloneAsync(
        string originUrl,
        string destinationPath,
        string? branch = null,
        int? depth = null,
        CancellationToken cancellationToken = default);

    Task FetchAsync(string mirrorPath, CancellationToken cancellationToken = default);

    Task WorktreeAddAsync(
        string mirrorPath,
        string worktreePath,
        string branchName,
        string? startPoint = null,
        CancellationToken cancellationToken = default);

    Task WorktreeRemoveAsync(
        string mirrorPath,
        string worktreePath,
        bool force = false,
        CancellationToken cancellationToken = default);

    Task CreateBranchAsync(
        string worktreePath,
        string branchName,
        string? startPoint = null,
        CancellationToken cancellationToken = default);

    Task CheckoutAsync(string worktreePath, string branchOrRef, CancellationToken cancellationToken = default);

    Task AddAsync(
        string worktreePath,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default);

    Task<bool> CommitAsync(string worktreePath, string message, CancellationToken cancellationToken = default);

    Task PushAsync(
        string worktreePath,
        string? remote = null,
        string? branch = null,
        CancellationToken cancellationToken = default);

    Task<string> RevParseAsync(string worktreePath, string rev, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the URL of an existing remote on the worktree, equivalent to
    /// <c>git remote set-url &lt;remote&gt; &lt;url&gt;</c>. Used by <c>vcs.clone</c> to scrub
    /// any auth-bearing URL out of <c>.git/config</c> after the initial fetch.
    /// </summary>
    Task SetRemoteUrlAsync(
        string worktreePath,
        string remoteName,
        string url,
        CancellationToken cancellationToken = default);

    Task<string> GetSymbolicHeadAsync(string gitDirectory, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> LsFilesAsync(string worktreePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitStatusEntry>> StatusAsync(string worktreePath, CancellationToken cancellationToken = default);
}

public sealed record GitStatusEntry(string StatusCode, string Path);

/// <summary>
/// Result of <see cref="IGitCli.CloneAsync"/>: the resolved branch (defaults if none requested),
/// the HEAD commit SHA after the clone settles, and the upstream's default branch (so callers can
/// surface it without a follow-up `git symbolic-ref` round trip).
/// </summary>
public sealed record GitCloneResult(string Branch, string HeadCommit, string DefaultBranch);
