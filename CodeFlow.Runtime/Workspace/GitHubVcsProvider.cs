using Octokit;

namespace CodeFlow.Runtime.Workspace;

/// <summary>
/// Octokit-backed GitHub <see cref="IVcsProvider"/>. Constructed per-call by the
/// <see cref="IVcsProviderFactory"/> with the decrypted token already in hand — providers do not
/// hold long-lived references to credentials, and there is no module-level singleton.
/// Errors normalize into the <c>VcsUnauthorized / VcsRepoNotFound / VcsConflict / VcsRateLimited</c>
/// taxonomy regardless of the underlying Octokit exception type, so callers can rely on a
/// stable surface independent of the library's internals.
/// </summary>
public sealed class GitHubVcsProvider : VcsProviderBase
{
    private const string UserAgent = "CodeFlow";

    private readonly string token;

    public GitHubVcsProvider(string token)
        : base(providerTag: "github")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        this.token = token;
    }

    public override GitHostMode Mode => GitHostMode.GitHub;

    public override async Task<VcsRepoMetadata> GetRepoMetadataAsync(
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidateOwnerName(owner, name);

        using var activity = StartActivity("vcs.github.get_repo", owner, name);
        var client = CreateClient();

        try
        {
            var repo = await client.Repository.Get(owner, name);
            return new VcsRepoMetadata(
                DefaultBranch: repo.DefaultBranch,
                CloneUrl: repo.CloneUrl,
                Visibility: MapVisibility(repo.Visibility, repo.Private));
        }
        catch (Exception ex)
        {
            throw GitHubErrorMapping.Translate(ex, owner, name);
        }
    }

    public override async Task<PullRequestInfo> OpenPullRequestAsync(
        string owner,
        string name,
        string head,
        string baseRef,
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        ValidatePullRequestInputs(owner, name, head, baseRef, title);

        using var activity = StartActivity("vcs.github.open_pr", owner, name);
        activity?.SetTag("vcs.github.head", head);
        activity?.SetTag("vcs.github.base", baseRef);

        var client = CreateClient();

        try
        {
            var request = new NewPullRequest(title, head, baseRef)
            {
                Body = body ?? string.Empty,
            };
            var pr = await client.PullRequest.Create(owner, name, request);
            return new PullRequestInfo(pr.HtmlUrl, pr.Number);
        }
        catch (Exception ex)
        {
            throw GitHubErrorMapping.Translate(ex, owner, name);
        }
    }

    private GitHubClient CreateClient() =>
        new(new ProductHeaderValue(UserAgent))
        {
            Credentials = new Credentials(token),
        };

    private static VcsRepoVisibility MapVisibility(RepositoryVisibility? visibility, bool isPrivate)
    {
        if (visibility.HasValue)
        {
            return visibility.Value switch
            {
                RepositoryVisibility.Public => VcsRepoVisibility.Public,
                RepositoryVisibility.Private => VcsRepoVisibility.Private,
                RepositoryVisibility.Internal => VcsRepoVisibility.Internal,
                _ => VcsRepoVisibility.Unknown,
            };
        }

        return isPrivate ? VcsRepoVisibility.Private : VcsRepoVisibility.Public;
    }
}

internal static class GitHubErrorMapping
{
    public static VcsException Translate(Exception ex, string owner, string name) =>
        ex switch
        {
            NotFoundException => new VcsRepoNotFoundException(owner, name),
            AuthorizationException ae => new VcsUnauthorizedException(ae.Message, ae),
            RateLimitExceededException rl => new VcsRateLimitedException(rl.Message, rl),
            ApiValidationException av => MapValidation(av),
            ApiException ae when ae.StatusCode == System.Net.HttpStatusCode.Unauthorized =>
                new VcsUnauthorizedException(ae.Message, ae),
            ApiException ae when ae.StatusCode == System.Net.HttpStatusCode.Forbidden =>
                new VcsUnauthorizedException(ae.Message, ae),
            ApiException ae when ae.StatusCode == System.Net.HttpStatusCode.Conflict =>
                new VcsConflictException(ae.Message, ae),
            ApiException ae => new GitHubApiException(ae.Message, ae),
            _ => new GitHubApiException(ex.Message, ex),
        };

    private static VcsException MapValidation(ApiValidationException ex)
    {
        var message = ex.ApiError?.Errors is { Count: > 0 } errs
            ? string.Join("; ", errs.Select(e => e.Message ?? e.Code ?? "validation failed"))
            : ex.Message;

        return new VcsConflictException(message, ex);
    }

    public sealed class GitHubApiException : VcsException
    {
        public GitHubApiException(string message, Exception inner) : base(message, inner) { }
    }
}
