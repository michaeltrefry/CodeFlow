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
    ///
    /// <paramref name="environmentVariables"/> is the per-trace credential env (epic 658) built
    /// by <see cref="GitCredentialEnv.Build"/>; when non-empty, the entries are added to the
    /// spawned <c>git</c> process so it picks up <c>credential.helper = store --file=...</c>
    /// without mutating any global gitconfig. Passing <c>null</c> (or an empty dictionary)
    /// preserves the legacy unauthenticated path for tests that don't need it.
    /// </summary>
    Task<GitCloneResult> CloneAsync(
        string originUrl,
        string destinationPath,
        string? branch = null,
        int? depth = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
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

    /// <summary>
    /// Pushes <paramref name="branch"/> to <paramref name="remote"/> from
    /// <paramref name="worktreePath"/>. <paramref name="environmentVariables"/> is the per-trace
    /// credential env (epic 658) — when non-empty the spawned <c>git push</c> picks up the
    /// store credential helper, so private-repo pushes succeed without any token in argv or
    /// <c>.git/config</c>. Passing <c>null</c> preserves the legacy unauthenticated path for
    /// platform-internal callers (mirror cache, tests).
    /// </summary>
    Task PushAsync(
        string worktreePath,
        string? remote = null,
        string? branch = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the upstream default branch by running
    /// <c>git ls-remote --symref &lt;remote&gt; HEAD</c> against the configured remote. Parses the
    /// <c>ref: refs/heads/&lt;X&gt;\tHEAD</c> line and returns <c>&lt;X&gt;</c>. The remote talk
    /// requires auth, so callers must thread the per-trace credential env in
    /// <paramref name="environmentVariables"/>; without it private-repo lookups fail with a git
    /// auth error. Throws <see cref="GitCommandException"/> on failure (network, auth) and
    /// <see cref="InvalidOperationException"/> when the response doesn't carry a symref line.
    /// </summary>
    Task<string> GetRemoteHeadBranchAsync(
        string worktreePath,
        string? remote = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default);

    Task<string> RevParseAsync(string worktreePath, string rev, CancellationToken cancellationToken = default);

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
