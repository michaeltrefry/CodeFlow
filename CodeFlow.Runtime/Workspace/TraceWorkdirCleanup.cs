using Microsoft.Extensions.Logging;

namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Filesystem-only helper for removing a trace's per-correlation working directory. Pure side
/// effects — no database, no settings lookups; callers resolve <c>WorkingDirectoryRoot</c>
/// upstream and pass it in. Used by trace-delete (Slice D) and happy-path completion cleanup
/// (Slice E) so both paths follow identical semantics: tolerate missing config, missing dir,
/// and IO/permission errors; surface other exceptions to callers.
/// </summary>
public static class TraceWorkdirCleanup
{
    public static bool TryRemove(string? workingDirectoryRoot, Guid traceId, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectoryRoot))
        {
            return false;
        }

        var workDir = Path.Combine(workingDirectoryRoot, traceId.ToString("N"));
        if (!Directory.Exists(workDir))
        {
            return false;
        }

        try
        {
            Directory.Delete(workDir, recursive: true);
            logger?.LogInformation(
                "Deleted trace workdir {WorkDir} for trace {TraceId}.",
                workDir,
                traceId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(
                ex,
                "Failed to delete workdir {WorkDir} for trace {TraceId}; left for sweep.",
                workDir,
                traceId);
            return false;
        }
    }
}
