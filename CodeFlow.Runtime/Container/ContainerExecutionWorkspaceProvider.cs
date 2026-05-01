using Microsoft.Extensions.Logging;

namespace CodeFlow.Runtime.Container;

/// <summary>
/// Per-workflow disposable execution workspace for <c>container.run</c>. The canonical
/// CodeFlow workspace stays the source of truth for agent edits (read_file/apply_patch);
/// container builds/tests operate on a writable copy mounted at <c>/workspace</c> so build
/// artifacts (compiled binaries, generated files) never leak back into the canonical tree.
///
/// Sync semantics: on each tool call we delta-mirror canonical → exec. New/changed files
/// in canonical (the agent's apply_patch edits between invocations) propagate forward; files
/// only present in exec (build artifacts produced by an earlier <c>container.run</c>) are
/// preserved across calls within the workflow. The exec dir is removed at workflow cleanup;
/// stale dirs from crashed runs are reaped by the orphan-TTL sweep.
/// </summary>
public sealed class ContainerExecutionWorkspaceProvider
{
    private readonly string executionRootPath;
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly ILogger<ContainerExecutionWorkspaceProvider>? logger;

    public ContainerExecutionWorkspaceProvider(
        string executionRootPath,
        Func<DateTimeOffset>? nowProvider = null,
        ILogger<ContainerExecutionWorkspaceProvider>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(executionRootPath))
        {
            throw new ArgumentException(
                "ContainerExecutionWorkspaceProvider requires a non-empty execution root path.",
                nameof(executionRootPath));
        }

        this.executionRootPath = Path.GetFullPath(executionRootPath);
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        this.logger = logger;
    }

    public string ExecutionRootPath => executionRootPath;

    public string GetWorkflowPath(Guid workflowId) =>
        Path.Combine(executionRootPath, workflowId.ToString("N"));

    /// <summary>
    /// Ensures the per-workflow execution workspace exists and reflects the canonical
    /// workspace. Returns the host path to mount at <c>/workspace</c>.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">
    /// Thrown when <paramref name="canonicalWorkspacePath"/> does not exist; callers should
    /// surface this as a structured refusal so the agent learns the workspace isn't ready.
    /// </exception>
    public string EnsureForWorkflow(Guid workflowId, string canonicalWorkspacePath)
    {
        if (string.IsNullOrWhiteSpace(canonicalWorkspacePath))
        {
            throw new ArgumentException("Canonical workspace path is required.", nameof(canonicalWorkspacePath));
        }

        var canonical = Path.GetFullPath(canonicalWorkspacePath);
        if (!Directory.Exists(canonical))
        {
            throw new DirectoryNotFoundException(
                $"Canonical workspace '{canonical}' does not exist; container execution workspace cannot be prepared.");
        }

        var executionPath = GetWorkflowPath(workflowId);
        Directory.CreateDirectory(executionPath);
        MirrorIncrementally(canonical, executionPath);
        return executionPath;
    }

    public bool RemoveWorkflow(Guid workflowId)
    {
        var executionPath = GetWorkflowPath(workflowId);
        if (!Directory.Exists(executionPath))
        {
            return false;
        }

        try
        {
            Directory.Delete(executionPath, recursive: true);
            logger?.LogInformation(
                "Removed container execution workspace at {Path} for workflow {WorkflowId}.",
                executionPath,
                workflowId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(
                ex,
                "Failed to remove container execution workspace at {Path}; orphan sweep will retry.",
                executionPath);
            return false;
        }
    }

    /// <summary>
    /// Reaps execution workspace directories whose mtime is older than <paramref name="maxAge"/>.
    /// Returns the count actually removed. Failures (IO, permissions) are logged and skipped.
    /// </summary>
    public int SweepOrphans(TimeSpan maxAge, DateTimeOffset? now = null)
    {
        if (maxAge <= TimeSpan.Zero || !Directory.Exists(executionRootPath))
        {
            return 0;
        }

        var cutoff = (now ?? nowProvider()) - maxAge;
        var deleted = 0;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories(executionRootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(
                ex,
                "Could not enumerate container execution workspace root {Root}.",
                executionRootPath);
            return 0;
        }

        foreach (var entry in entries)
        {
            DateTime mtime;
            try
            {
                mtime = Directory.GetLastWriteTimeUtc(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Could not stat execution workspace {Entry}; skipping.", entry);
                continue;
            }

            if (mtime > cutoff.UtcDateTime)
            {
                continue;
            }

            try
            {
                Directory.Delete(entry, recursive: true);
                deleted++;
                logger?.LogInformation(
                    "Swept stale container execution workspace {Entry} (mtime {Mtime}).",
                    entry,
                    mtime);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(
                    ex,
                    "Failed to delete stale execution workspace {Entry}; will retry next cycle.",
                    entry);
            }
        }

        return deleted;
    }

    private static void MirrorIncrementally(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(sourceFile);
            var destFile = Path.Combine(destDir, name);

            if (!File.Exists(destFile))
            {
                File.Copy(sourceFile, destFile, overwrite: false);
                continue;
            }

            var sourceInfo = new FileInfo(sourceFile);
            var destInfo = new FileInfo(destFile);
            if (sourceInfo.LastWriteTimeUtc > destInfo.LastWriteTimeUtc
                || sourceInfo.Length != destInfo.Length)
            {
                File.Copy(sourceFile, destFile, overwrite: true);
            }
        }

        foreach (var sourceSub in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(sourceSub);
            var destSub = Path.Combine(destDir, name);
            MirrorIncrementally(sourceSub, destSub);
        }
    }
}
