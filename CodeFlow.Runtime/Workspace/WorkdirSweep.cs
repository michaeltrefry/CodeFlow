using Microsoft.Extensions.Logging;

namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Filesystem-only sweep helper for orphan trace workdirs. Pure side effects — no DB, no
/// settings lookups; callers resolve <c>WorkingDirectoryRoot</c> + max-age upstream and pass them
/// in. Used by Slice F's periodic background service. Walks the configured root and deletes
/// entries whose mtime is older than <paramref name="maxAge"/> AND whose name matches the
/// per-trace <c>{traceId:N}</c> shape (32 lowercase hex chars). Reserved siblings under the same
/// root (e.g. the assistant per-conversation tree at <c>/workspace/assistant</c>, or the
/// container exec workspaces at <c>/workspace/container-workspace</c>) are intentionally
/// skipped — those have their own lifecycles.
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
            var name = Path.GetFileName(entry);
            if (!IsTraceWorkdirName(name))
            {
                // Reserved siblings (assistant/, container-workspace/, README, …) are not
                // managed by this sweep. Skip silently — operators occasionally land files
                // here by hand, and a noisy log per cycle is just noise.
                continue;
            }

            DateTime mtime;
            try
            {
                // {traceId:N} entries are always dirs, but check both forms for the same TTL
                // in case a hex-named regular file was dropped here by accident.
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

    /// <summary>
    /// True when <paramref name="name"/> matches the <c>Guid.ToString("N")</c> shape: exactly
    /// 32 lowercase hexadecimal characters. The CLR formats Guid "N" as lowercase, so
    /// uppercase hex is treated as out-of-spec and skipped.
    /// </summary>
    public static bool IsTraceWorkdirName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length != 32)
        {
            return false;
        }
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
            {
                return false;
            }
        }
        return true;
    }
}
