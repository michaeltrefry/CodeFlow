using System.Collections.Concurrent;

namespace CodeFlow.Runtime.Workspace;

public sealed class WorkspaceService : IWorkspaceService
{
    private readonly WorkspaceOptions options;
    private readonly IGitCli git;
    private readonly IRepoUrlHostGuard hostGuard;
    private readonly ConcurrentDictionary<(Guid CorrelationId, string RepoSlug), Workspace> workspaces = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> mirrorLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Guid, string), SemaphoreSlim> openLocks = new();

    public WorkspaceService(WorkspaceOptions options, IGitCli git, IRepoUrlHostGuard? hostGuard = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(git);

        this.options = options;
        this.git = git;
        this.hostGuard = hostGuard ?? new PermissiveRepoUrlHostGuard();
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
        var key = (correlationId, repo.Slug);

        var openLock = openLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await openLock.WaitAsync(cancellationToken);
        try
        {
            if (workspaces.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var mirrorPath = Path.Combine(options.CachePath, repo.MirrorRelativePath);
            await EnsureMirrorAsync(repo, mirrorPath, repoUrl, cancellationToken);

            var defaultBranch = await ResolveDefaultBranchAsync(mirrorPath, cancellationToken);
            var effectiveBase = string.IsNullOrWhiteSpace(baseBranch) ? defaultBranch : baseBranch!;

            var worktreePath = Path.Combine(options.WorkPath, correlationId.ToString("N"), repo.Slug);
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
            openLock.Release();
        }
    }

    public Workspace? Get(Guid correlationId, string repoSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoSlug);
        return workspaces.TryGetValue((correlationId, repoSlug), out var workspace) ? workspace : null;
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

            try
            {
                await git.WorktreeRemoveAsync(
                    workspace.MirrorPath,
                    workspace.RootPath,
                    force: true,
                    cancellationToken);
            }
            catch (GitCommandException)
            {
            }

            if (Directory.Exists(workspace.RootPath))
            {
                try
                {
                    Directory.Delete(workspace.RootPath, recursive: true);
                }
                catch
                {
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
            catch
            {
            }
        }
    }

    private async Task EnsureMirrorAsync(
        RepoReference repo,
        string mirrorPath,
        string repoUrl,
        CancellationToken cancellationToken)
    {
        var lockKey = repo.MirrorRelativePath;
        var mirrorLock = mirrorLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

        await mirrorLock.WaitAsync(cancellationToken);
        try
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
        finally
        {
            mirrorLock.Release();
        }
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
