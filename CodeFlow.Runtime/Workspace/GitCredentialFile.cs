using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Per-trace git credential file management (epic 658). The file format is git's native
/// <c>credential-store</c> wire format — one URL per line, optional userinfo segment carrying
/// the auth — so <c>git</c> reads it directly via <c>credential.helper = store --file=&lt;path&gt;</c>
/// with no custom helper script in between.
///
/// Pure side effects: this helper does not read settings, does not call into the VCS provider
/// factory, does not own the trace lifecycle. Callers resolve <see cref="WorkspaceOptions.GitCredentialRoot"/>
/// + the relevant <see cref="HostCredential"/> list upstream and pass them in. Used by
/// the trace-start hook in <c>TracesEndpoints</c>, the trace-cleanup hooks (<c>TraceWorkdirCleanup</c>'s
/// sibling cred-removal path), and the periodic sweep.
///
/// File path is <c>{credentialRoot}/{traceId:N}</c>. Mode is forced to <c>0600</c> on Unix
/// (no-op on Windows). Parent dir is expected to already exist at <c>0700</c> owned by the
/// app uid — the Dockerfile sets that up at image build, and operators with an overridden
/// <see cref="WorkspaceOptions.GitCredentialRoot"/> need to do the same out-of-band.
/// </summary>
public static class GitCredentialFile
{
    private const UnixFileMode FileMode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Writes the per-trace credential file. Overwrites any existing file at the same path
    /// (idempotent: a re-published trace start replaces the old creds rather than appending).
    /// When <paramref name="credentials"/> is empty the file is removed instead — an empty
    /// store would still satisfy <c>credential.helper = store</c> but emits a confusing
    /// "file does not exist; will create" log on every git op, and it's clearer to leave the
    /// path absent.
    /// </summary>
    public static async Task WriteAsync(
        string credentialRoot,
        Guid traceId,
        IReadOnlyList<HostCredential> credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialRoot);
        ArgumentNullException.ThrowIfNull(credentials);

        var path = BuildPath(credentialRoot, traceId);

        if (credentials.Count == 0)
        {
            TryDelete(path);
            return;
        }

        Directory.CreateDirectory(credentialRoot);

        var buffer = new StringBuilder();
        foreach (var cred in credentials)
        {
            buffer.AppendLine(FormatStoreLine(cred));
        }

        await File.WriteAllTextAsync(path, buffer.ToString(), Encoding.UTF8, cancellationToken);
        TrySetMode0600(path);
    }

    /// <summary>
    /// Removes the per-trace credential file if present. Returns <c>true</c> when an entry
    /// was deleted. Tolerates missing root, missing file, IO/permission errors — those are
    /// left for the periodic sweep, matching <see cref="TraceWorkdirCleanup"/>'s contract.
    /// </summary>
    public static bool TryRemove(string? credentialRoot, Guid traceId, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(credentialRoot))
        {
            return false;
        }

        var path = BuildPath(credentialRoot, traceId);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            logger?.LogInformation(
                "Deleted trace git-credential file {Path} for trace {TraceId}.",
                path,
                traceId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(
                ex,
                "Failed to delete git-credential file {Path} for trace {TraceId}; left for sweep.",
                path,
                traceId);
            return false;
        }
    }

    /// <summary>
    /// Filesystem-only sweep helper for orphan trace credential files. Mirror of
    /// <see cref="WorkdirSweep.Sweep"/>: walks the configured root, deletes entries whose
    /// mtime is older than <paramref name="maxAge"/> and whose name matches the
    /// <c>{traceId:N}</c> shape. Returns the count of entries deleted.
    /// </summary>
    public static int Sweep(
        string? credentialRoot,
        TimeSpan maxAge,
        DateTimeOffset now,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(credentialRoot)
            || !Directory.Exists(credentialRoot))
        {
            return 0;
        }

        var cutoff = now - maxAge;
        var deleted = 0;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(credentialRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(
                ex,
                "Could not enumerate git-credential root {Root} for sweep.",
                credentialRoot);
            return 0;
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (!WorkdirSweep.IsTraceWorkdirName(name))
            {
                continue;
            }

            DateTime mtime;
            try
            {
                mtime = File.GetLastWriteTimeUtc(entry);
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
                File.Delete(entry);
                deleted++;
                logger?.LogInformation(
                    "Swept stale git-credential file {Entry} (mtime {Mtime}).",
                    entry,
                    mtime);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(
                    ex,
                    "Failed to delete stale git-credential file {Entry}; will retry next cycle.",
                    entry);
            }
        }

        return deleted;
    }

    /// <summary>
    /// Returns the absolute file path for the per-trace credential file. Used by both the
    /// writer/remover above and by the env-var builder that points <c>credential.helper</c>
    /// at the file when spawning <c>git</c>.
    /// </summary>
    public static string BuildPath(string credentialRoot, Guid traceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialRoot);
        return Path.Combine(credentialRoot, traceId.ToString("N", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Builds one line of git's credential-store wire format from a parsed
    /// <see cref="HostCredential"/>. Userinfo gets URL-encoded so opaque tokens carrying
    /// reserved characters (<c>@</c>, <c>:</c>, <c>/</c>, etc.) round-trip correctly.
    /// </summary>
    internal static string FormatStoreLine(HostCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var protocol = string.IsNullOrWhiteSpace(credential.Protocol) ? "https" : credential.Protocol;
        var user = Uri.EscapeDataString(credential.Username);
        var token = Uri.EscapeDataString(credential.Token);
        return $"{protocol}://{user}:{token}@{credential.Host}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. Sweep will retry.
        }
    }

    private static void TrySetMode0600(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, FileMode0600);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: parent dir is 0700 owned by APP_UID per the Dockerfile, so even
            // if we can't tighten file mode to 0600 the file is still unreachable to other
            // uids. Don't fail the trace-start path on a chmod hiccup.
        }
    }
}

/// <summary>
/// Parsed credential entry for one git host. <see cref="Username"/> is conventionally
/// <c>x-access-token</c> for GitHub PATs and <c>oauth2</c> for GitLab tokens — both hosts
/// accept any non-empty username when a token sits in the password slot, so the choice is
/// cosmetic.
/// </summary>
public sealed record HostCredential(string Host, string Username, string Token, string Protocol = "https");
