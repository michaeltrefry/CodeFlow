using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Runtime.Workspace;

public sealed class WorkspaceService : IWorkspaceService
{
    private readonly WorkspaceOptions options;
    private readonly IGitCli git;
    private readonly IRepoUrlHostGuard hostGuard;
    private readonly ILogger<WorkspaceService> logger;
    private readonly ConcurrentDictionary<(Guid CorrelationId, string RepoIdentityKey), Workspace> workspaces = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> mirrorLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Guid, string), SemaphoreSlim> openLocks = new();

    public WorkspaceService(
        WorkspaceOptions options,
        IGitCli git,
        IRepoUrlHostGuard? hostGuard = null,
        ILogger<WorkspaceService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(git);

        this.options = options;
        this.git = git;
        this.hostGuard = hostGuard ?? new PermissiveRepoUrlHostGuard();
        this.logger = logger ?? NullLogger<WorkspaceService>.Instance;
    }

    public async Task<Workspace> OpenAsync(
        Guid correlationId,
        string repoUrl,
        string? baseBranch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoUrl);
        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("correlationId must be non-empty.", nameof(correlationId));
        }

        var repo = RepoReference.Parse(repoUrl);
        await hostGuard.AssertAllowedAsync(repo, cancellationToken);
        var key = (correlationId, repo.IdentityKey);

        var openLock = openLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await openLock.WaitAsync(cancellationToken);
        try
        {
            if (workspaces.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var mirrorPath = Path.Combine(options.CachePath, repo.MirrorRelativePath);

            // Hold the per-mirror lock across the entire mirror-touching span: clone-or-fetch,
            // default-branch resolution, and `git worktree add`. Branch names are unique per
            // correlation so logical writers don't collide, but git serializes on per-repo
            // lock files (refs/heads/.lock, worktrees/<name>/HEAD, index.lock) and parallel
            // `worktree add` against the same mirror can fail with "invalid reference" when
            // those locks race during ref creation. Serializing here trades a small amount of
            // open-time concurrency for correctness on the shared bare repo.
            var mirrorLock = mirrorLocks.GetOrAdd(repo.MirrorRelativePath, _ => new SemaphoreSlim(1, 1));
            await mirrorLock.WaitAsync(cancellationToken);
            try
            {
                await EnsureMirrorCoreAsync(mirrorPath, repoUrl, cancellationToken);

                var defaultBranch = await ResolveDefaultBranchAsync(mirrorPath, cancellationToken);
                var effectiveBase = string.IsNullOrWhiteSpace(baseBranch) ? defaultBranch : baseBranch!;

                var worktreePath = Path.Combine(options.WorkPath, correlationId.ToString("N"), repo.IdentityKey);
                Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

                var branchName = BuildWorkBranchName(correlationId, repo);

                await git.WorktreeAddAsync(
                    mirrorPath,
                    worktreePath,
                    branchName,
                    startPoint: effectiveBase,
                    cancellationToken);

                var workspace = new Workspace(
                    correlationId,
                    repo,
                    repoUrl,
                    worktreePath,
                    defaultBranch,
                    branchName,
                    mirrorPath);

                workspaces[key] = workspace;
                return workspace;
            }
            finally
            {
                mirrorLock.Release();
            }
        }
        finally
        {
            openLock.Release();
        }
    }

    public Workspace? Get(Guid correlationId, string repoSlugOrIdentityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoSlugOrIdentityKey);
        // Accepts either the identity key (collision-free) or the slug (human-readable). The
        // dictionary is keyed by identity key; slug lookups walk the open workspaces for this
        // correlation and match on either to preserve backwards compatibility with callers that
        // only know the slug.
        if (workspaces.TryGetValue((correlationId, repoSlugOrIdentityKey), out var direct))
        {
            return direct;
        }

        foreach (var ((corr, _), workspace) in workspaces)
        {
            if (corr == correlationId
                && (string.Equals(workspace.RepoSlug, repoSlugOrIdentityKey, StringComparison.Ordinal)
                    || string.Equals(workspace.RepoIdentityKey, repoSlugOrIdentityKey, StringComparison.Ordinal)))
            {
                return workspace;
            }
        }

        return null;
    }

    public async Task ReleaseAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        if (correlationId == Guid.Empty)
        {
            return;
        }

        var keysToRemove = workspaces.Keys
            .Where(k => k.CorrelationId == correlationId)
            .ToArray();

        foreach (var key in keysToRemove)
        {
            if (!workspaces.TryRemove(key, out var workspace))
            {
                continue;
            }

            var worktreeRemoved = true;
            try
            {
                await git.WorktreeRemoveAsync(
                    workspace.MirrorPath,
                    workspace.RootPath,
                    force: true,
                    cancellationToken);
            }
            catch (GitCommandException ex)
            {
                worktreeRemoved = false;
                logger.LogWarning(
                    ex,
                    "git worktree remove failed for workspace {RootPath} (correlation {CorrelationId}, repo {RepoIdentityKey}); will still attempt directory cleanup.",
                    workspace.RootPath,
                    correlationId,
                    workspace.RepoIdentityKey);
            }

            // `git worktree remove --force` already deletes the directory on success, so the
            // fallback Directory.Delete only runs when we genuinely still have orphaned files
            // — either because the git command failed, or because the directory survived on a
            // filesystem that races with git's flush.
            if (!worktreeRemoved && Directory.Exists(workspace.RootPath))
            {
                try
                {
                    Directory.Delete(workspace.RootPath, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(
                        ex,
                        "Directory.Delete fallback failed for workspace {RootPath} (correlation {CorrelationId}); disk may leak until manual cleanup.",
                        workspace.RootPath,
                        correlationId);
                }
            }

            openLocks.TryRemove(key, out _);
        }

        var correlationDir = Path.Combine(options.WorkPath, correlationId.ToString("N"));
        if (Directory.Exists(correlationDir))
        {
            try
            {
                Directory.Delete(correlationDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    ex,
                    "Failed to remove correlation directory {CorrelationDir} (correlation {CorrelationId}); disk may leak until manual cleanup.",
                    correlationDir,
                    correlationId);
            }
        }
    }

    /// <summary>
    /// Clone or fetch the bare mirror at <paramref name="mirrorPath"/>. Caller MUST hold the
    /// per-mirror semaphore from <c>mirrorLocks</c> before invoking this — the previous
    /// inline lock acquisition was widened into <see cref="OpenAsync"/> so the mirror lock
    /// covers the subsequent <c>git worktree add</c> as well.
    /// </summary>
    private async Task EnsureMirrorCoreAsync(
        string mirrorPath,
        string repoUrl,
        CancellationToken cancellationToken)
    {
        if (IsMirrorReady(mirrorPath))
        {
            await git.FetchAsync(mirrorPath, cancellationToken);
            return;
        }

        var mirrorParent = Path.GetDirectoryName(mirrorPath);
        if (!string.IsNullOrEmpty(mirrorParent))
        {
            Directory.CreateDirectory(mirrorParent);
        }

        if (Directory.Exists(mirrorPath))
        {
            Directory.Delete(mirrorPath, recursive: true);
        }

        await git.CloneMirrorAsync(repoUrl, mirrorPath, cancellationToken);
    }

    private static bool IsMirrorReady(string mirrorPath)
    {
        return Directory.Exists(mirrorPath) && File.Exists(Path.Combine(mirrorPath, "HEAD"));
    }

    private async Task<string> ResolveDefaultBranchAsync(
        string mirrorPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var head = await git.GetSymbolicHeadAsync(mirrorPath, cancellationToken);
            return string.IsNullOrWhiteSpace(head) ? "main" : head;
        }
        catch (GitCommandException)
        {
            return "main";
        }
    }

    private static string BuildWorkBranchName(Guid correlationId, RepoReference repo)
    {
        var shortCorrelation = correlationId.ToString("N")[..8];
        var sanitizedSlug = repo.Slug
            .Replace('/', '-')
            .Replace('\\', '-')
            .Replace(' ', '-');
        return $"codeflow/wt/{shortCorrelation}/{sanitizedSlug}";
    }
}
