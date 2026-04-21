using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Runtime.Workspace;

public sealed class WorktreeSweeper
{
    private readonly WorkspaceOptions options;
    private readonly IGitCli git;
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly ILogger logger;

    public WorktreeSweeper(
        WorkspaceOptions options,
        IGitCli git,
        Func<DateTimeOffset>? nowProvider = null,
        ILogger<WorktreeSweeper>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(git);

        this.options = options;
        this.git = git;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        this.logger = logger ?? NullLogger<WorktreeSweeper>.Instance;
    }

    public async Task<WorktreeSweepReport> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(options.WorkPath))
        {
            return new WorktreeSweepReport(0, 0);
        }

        var cutoff = nowProvider() - options.WorktreeTtl;
        var correlationsPurged = 0;

        foreach (var correlationDir in Directory.EnumerateDirectories(options.WorkPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new DirectoryInfo(correlationDir);
            var lastTouched = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (lastTouched > cutoff)
            {
                continue;
            }

            try
            {
                Directory.Delete(correlationDir, recursive: true);
                correlationsPurged++;
                logger.LogInformation(
                    "workspace.sweep.purged {CorrelationDir} lastWrite={LastWriteUtc:o}",
                    correlationDir,
                    info.LastWriteTimeUtc);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "workspace.sweep.failed {CorrelationDir}",
                    correlationDir);
            }
        }

        var mirrorsPruned = 0;
        if (Directory.Exists(options.CachePath))
        {
            foreach (var mirror in EnumerateMirrors(options.CachePath))
            {
                try
                {
                    await git.WorktreePruneAsync(mirror, cancellationToken);
                    mirrorsPruned++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "workspace.sweep.prune_failed {Mirror}",
                        mirror);
                }
            }
        }

        logger.LogInformation(
            "workspace.sweep.complete correlationsPurged={Purged} mirrorsPruned={Pruned}",
            correlationsPurged,
            mirrorsPruned);

        return new WorktreeSweepReport(correlationsPurged, mirrorsPruned);
    }

    private static IEnumerable<string> EnumerateMirrors(string cacheRoot)
    {
        foreach (var dir in Directory.EnumerateDirectories(cacheRoot, "*.git", SearchOption.AllDirectories))
        {
            if (File.Exists(Path.Combine(dir, "HEAD")))
            {
                yield return dir;
            }
        }
    }
}

public sealed record WorktreeSweepReport(int CorrelationsPurged, int MirrorsPruned);
