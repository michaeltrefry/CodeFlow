using CodeFlow.Runtime.Observability;
using Activity = System.Diagnostics.Activity;

namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Shared base for <see cref="IVcsProvider"/> implementations. Owns the cross-provider
/// boilerplate flagged in F-017 of the 2026-04-28 backend review: argument validation for the
/// owner/name pair and pull-request inputs, plus consistent activity-tag shape across providers
/// (so traces from GitHub and GitLab share <c>vcs.provider</c> / <c>vcs.repo.owner</c> /
/// <c>vcs.repo.name</c> regardless of which concrete client made the call).
/// </summary>
/// <remarks>
/// Each provider keeps its own HTTP/SDK call sequence and exception-to-<see cref="VcsException"/>
/// translation — those differ enough between Octokit (typed exceptions) and the GitLab raw-REST
/// path (status-code + body inspection) that hoisting a single <c>TranslateException</c> would
/// force one of the providers into an awkward shape. The base only consolidates what's
/// genuinely identical today.
/// </remarks>
public abstract class VcsProviderBase : IVcsProvider
{
    private readonly string providerTag;

    /// <param name="providerTag">Short identifier for the <c>vcs.provider</c> activity tag,
    /// e.g. <c>"github"</c> or <c>"gitlab"</c>. Lower-cased by convention.</param>
    protected VcsProviderBase(string providerTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerTag);
        this.providerTag = providerTag;
    }

    public abstract GitHostMode Mode { get; }

    public abstract Task<VcsRepoMetadata> GetRepoMetadataAsync(
        string owner,
        string name,
        CancellationToken cancellationToken = default);

    public abstract Task<PullRequestInfo> OpenPullRequestAsync(
        string owner,
        string name,
        string head,
        string baseRef,
        string title,
        string body,
        CancellationToken cancellationToken = default);

    public abstract string BuildAuthenticatedCloneUrl(string repoUrl);

    /// <summary>
    /// Embeds <c>username:token</c> basic-auth userinfo into <paramref name="repoUrl"/>'s
    /// authority. Refuses non-HTTPS URLs so a token never goes over plain HTTP, and refuses URLs
    /// that already carry userinfo to avoid surprising overrides. Used by both providers'
    /// <see cref="BuildAuthenticatedCloneUrl"/> implementations.
    /// </summary>
    protected static string EmbedBasicAuth(string repoUrl, string username, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Repository URL '{repoUrl}' is not absolute.", nameof(repoUrl));
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Repository URL must use https:// (was '{uri.Scheme}://').", nameof(repoUrl));
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                "Repository URL already contains user info; refusing to override.", nameof(repoUrl));
        }

        var encodedUser = Uri.EscapeDataString(username);
        var encodedToken = Uri.EscapeDataString(token);
        var builder = new UriBuilder(uri)
        {
            UserName = encodedUser,
            Password = encodedToken,
        };
        return builder.Uri.ToString();
    }

    /// <summary>Validate the inputs to <see cref="GetRepoMetadataAsync"/>.</summary>
    protected static void ValidateOwnerName(string owner, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
    }

    /// <summary>Validate the inputs to <see cref="OpenPullRequestAsync"/>.
    /// Body is intentionally not validated — empty body is allowed.</summary>
    protected static void ValidatePullRequestInputs(
        string owner,
        string name,
        string head,
        string baseRef,
        string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(head);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
    }

    /// <summary>
    /// Start a child activity with the canonical VCS tag set: <c>vcs.provider</c>,
    /// <c>vcs.repo.owner</c>, <c>vcs.repo.name</c>. Derived providers add operation-specific
    /// tags (e.g. <c>vcs.github.head</c>) on the returned activity directly.
    /// </summary>
    protected Activity? StartActivity(string operationName, string owner, string repo)
    {
        var activity = CodeFlowActivity.StartChild(operationName);
        activity?.SetTag("vcs.provider", providerTag);
        activity?.SetTag("vcs.repo.owner", owner);
        activity?.SetTag("vcs.repo.name", repo);
        return activity;
    }
}
