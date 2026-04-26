using Microsoft.Extensions.Logging;

namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Filesystem-only sweep helper for orphan trace workdirs. Pure side effects — no DB, no
/// settings lookups; callers resolve <c>WorkingDirectoryRoot</c> + max-age upstream and pass them
/// in. Used by Slice F's periodic background service. Walks the configured root and deletes
/// entries whose mtime is older than <paramref name="maxAge"/>.
/// </summary>
public static class WorkdirSweep
{
    /// <summary>Default TTL applied when <c>WorkingDirectoryMaxAgeDays</c> is unset.</summary>
    public const int DefaultMaxAgeDays = 14;

    /// <summary>Returns the count of entries deleted.</summary>
    public static int Sweep(
        string? workingDirectoryRoot,
        TimeSpan maxAge,
        DateTimeOffset now,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectoryRoot)
            || !Directory.Exists(workingDirectoryRoot))
        {
            return 0;
        }

        var cutoff = now - maxAge;
        var deleted = 0;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(workingDirectoryRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(
                ex,
                "Could not enumerate workdir root {Root} for sweep.",
                workingDirectoryRoot);
            return 0;
        }

        foreach (var entry in entries)
        {
            DateTime mtime;
            try
            {
                // Walk both files and dirs; trace workdirs are dirs but operators sometimes drop
                // files at the root by accident, so we sweep both with the same TTL.
                mtime = File.Exists(entry)
                    ? File.GetLastWriteTimeUtc(entry)
                    : Directory.GetLastWriteTimeUtc(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Could not stat {Entry}; skipping.", entry);
                continue;
            }

            if (mtime > cutoff.UtcDateTime)
            {
                continue;
            }

            try
            {
                if (File.Exists(entry))
                {
                    File.Delete(entry);
                }
                else
                {
                    Directory.Delete(entry, recursive: true);
                }
                deleted++;
                logger?.LogInformation(
                    "Swept stale workdir entry {Entry} (mtime {Mtime}).",
                    entry,
                    mtime);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(
                    ex,
                    "Failed to delete stale entry {Entry}; will retry next cycle.",
                    entry);
            }
        }

        return deleted;
    }
}
