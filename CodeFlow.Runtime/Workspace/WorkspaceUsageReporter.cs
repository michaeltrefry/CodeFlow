namespace CodeFlow.Runtime.Workspace;

public sealed record WorkspaceUsage(
    long RootBytes,
    long CacheBytes,
    int WorktreeCount,
    long WarnThresholdBytes,
    bool AboveWarn);

public interface IWorkspaceUsageReporter
{
    Task<WorkspaceUsage> GetUsageAsync(CancellationToken cancellationToken = default);
}

public sealed class WorkspaceUsageReporter : IWorkspaceUsageReporter
{
    private readonly WorkspaceOptions options;
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private WorkspaceUsage? cached;
    private DateTimeOffset cachedAt;

    public WorkspaceUsageReporter(WorkspaceOptions options, Func<DateTimeOffset>? nowProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<WorkspaceUsage> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var now = nowProvider();
        if (cached is not null && now - cachedAt < options.DiskUsageCacheDuration)
        {
            return cached;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            now = nowProvider();
            if (cached is not null && now - cachedAt < options.DiskUsageCacheDuration)
            {
                return cached;
            }

            var rootBytes = SafeDirectorySize(options.Root);
            var cacheBytes = SafeDirectorySize(options.CachePath);
            var worktreeCount = CountWorktrees(options.WorkPath);
            var aboveWarn = rootBytes > options.DiskUsageWarnBytes;

            cached = new WorkspaceUsage(
                RootBytes: rootBytes,
                CacheBytes: cacheBytes,
                WorktreeCount: worktreeCount,
                WarnThresholdBytes: options.DiskUsageWarnBytes,
                AboveWarn: aboveWarn);
            cachedAt = now;
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    private static long SafeDirectorySize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return total;
    }

    private static int CountWorktrees(string workPath)
    {
        if (!Directory.Exists(workPath))
        {
            return 0;
        }

        var count = 0;
        foreach (var correlationDir in Directory.EnumerateDirectories(workPath))
        {
            count += Directory.EnumerateDirectories(correlationDir).Count();
        }

        return count;
    }
}
