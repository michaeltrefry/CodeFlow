namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Resolves per-trace git credentials from the configured <see cref="GitHostSettings"/> + the
/// trace's declared repository URLs (epic 658). Returns one <see cref="HostCredential"/> per
/// distinct git host across the inputs; when no git host is configured (or no token has been
/// stored) returns an empty list and the trace proceeds without git auth — git operations that
/// need to push will fail at the helper, with a clear "no credentials" error rather than a
/// silent token leak.
/// </summary>
public interface IPerTraceCredentialResolver
{
    /// <summary>
    /// Resolves credentials for the union of hosts named by <paramref name="repoUrls"/>. Invalid
    /// or unparseable URLs are skipped silently — the gate that admits repos into the trace
    /// already validates the URL shape, so this is defence-in-depth, not validation.
    /// </summary>
    Task<IReadOnlyList<HostCredential>> ResolveAsync(
        IReadOnlyList<string> repoUrls,
        CancellationToken cancellationToken = default);
}
