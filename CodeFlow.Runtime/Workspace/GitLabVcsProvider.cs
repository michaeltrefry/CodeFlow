using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeFlow.Runtime.Observability;
using Activity = System.Diagnostics.Activity;

namespace CodeFlow.Runtime.Workspace;

public sealed class GitLabVcsProvider : IVcsProvider
{
    private readonly HttpClient httpClient;
    private readonly IGitHostTokenProvider tokenProvider;
    private readonly IGitHostSettingsReader settingsReader;

    public GitLabVcsProvider(
        HttpClient httpClient,
        IGitHostTokenProvider tokenProvider,
        IGitHostSettingsReader settingsReader)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(settingsReader);

        this.httpClient = httpClient;
        this.tokenProvider = tokenProvider;
        this.settingsReader = settingsReader;
    }

    public GitHostMode Mode => GitHostMode.GitLab;

    public async Task<VcsRepoMetadata> GetRepoMetadataAsync(
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var activity = StartActivity("vcs.gitlab.get_repo", owner, name);
        var baseUri = await ResolveBaseUriAsync(cancellationToken);
        var projectPath = BuildProjectPath(owner, name);

        using var request = await CreateRequestAsync(HttpMethod.Get, baseUri, $"projects/{projectPath}", cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await TranslateAsync(response, owner, name, cancellationToken);
        }

        var project = await response.Content.ReadFromJsonAsync<GitLabProject>(JsonOptions, cancellationToken)
            ?? throw new VcsConflictException("GitLab returned an empty project payload.");

        return new VcsRepoMetadata(
            DefaultBranch: project.DefaultBranch ?? "main",
            CloneUrl: project.HttpUrlToRepo ?? string.Empty,
            Visibility: MapVisibility(project.Visibility));
    }

    public async Task<PullRequestInfo> OpenPullRequestAsync(
        string owner,
        string name,
        string head,
        string baseRef,
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(head);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        using var activity = StartActivity("vcs.gitlab.open_mr", owner, name);
        activity?.SetTag("vcs.gitlab.source_branch", head);
        activity?.SetTag("vcs.gitlab.target_branch", baseRef);

        var baseUri = await ResolveBaseUriAsync(cancellationToken);
        var projectPath = BuildProjectPath(owner, name);

        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            baseUri,
            $"projects/{projectPath}/merge_requests",
            cancellationToken);

        request.Content = JsonContent.Create(new
        {
            source_branch = head,
            target_branch = baseRef,
            title,
            description = body ?? string.Empty,
        }, options: JsonOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await TranslateAsync(response, owner, name, cancellationToken);
        }

        var mr = await response.Content.ReadFromJsonAsync<GitLabMergeRequest>(JsonOptions, cancellationToken)
            ?? throw new VcsConflictException("GitLab returned an empty merge-request payload.");

        return new PullRequestInfo(mr.WebUrl ?? string.Empty, mr.Iid);
    }

    private async Task<Uri> ResolveBaseUriAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsReader.GetAsync(cancellationToken)
            ?? throw new GitHostNotConfiguredException();

        if (settings.Mode != GitHostMode.GitLab)
        {
            throw new InvalidOperationException(
                $"GitLabVcsProvider invoked but mode is '{settings.Mode}'.");
        }

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            throw new GitHostNotConfiguredException();
        }

        var trimmed = settings.BaseUrl.TrimEnd('/');
        return new Uri(trimmed + "/api/v4/");
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        Uri baseUri,
        string relativePath,
        CancellationToken cancellationToken)
    {
        using var lease = await tokenProvider.AcquireAsync(cancellationToken);

        var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.Token);
        request.Headers.UserAgent.ParseAdd("CodeFlow");
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static string BuildProjectPath(string owner, string name)
        => Uri.EscapeDataString($"{owner}/{name}");

    private static Activity? StartActivity(string name, string owner, string repo)
    {
        var activity = CodeFlowActivity.StartChild(name);
        activity?.SetTag("vcs.provider", "gitlab");
        activity?.SetTag("vcs.repo.owner", owner);
        activity?.SetTag("vcs.repo.name", repo);
        return activity;
    }

    private static VcsRepoVisibility MapVisibility(string? visibility) => visibility switch
    {
        "public" => VcsRepoVisibility.Public,
        "private" => VcsRepoVisibility.Private,
        "internal" => VcsRepoVisibility.Internal,
        _ => VcsRepoVisibility.Unknown,
    };

    private static async Task<VcsException> TranslateAsync(
        HttpResponseMessage response,
        string owner,
        string name,
        CancellationToken cancellationToken)
    {
        var body = await SafeReadAsync(response, cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new VcsRepoNotFoundException(owner, name),
            HttpStatusCode.Unauthorized => new VcsUnauthorizedException(BuildMessage("401 Unauthorized", body)),
            HttpStatusCode.Forbidden => new VcsUnauthorizedException(BuildMessage("403 Forbidden", body)),
            HttpStatusCode.Conflict => new VcsConflictException(BuildMessage("409 Conflict", body)),
            HttpStatusCode.UnprocessableEntity => new VcsConflictException(BuildMessage("422 Unprocessable", body)),
            HttpStatusCode.TooManyRequests => new VcsRateLimitedException(BuildMessage("429 Too Many Requests", body)),
            _ => new VcsConflictException(BuildMessage($"HTTP {(int)response.StatusCode}", body)),
        };
    }

    private static string BuildMessage(string prefix, string body)
        => string.IsNullOrWhiteSpace(body) ? prefix : $"{prefix}: {body}";

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class GitLabProject
    {
        [JsonPropertyName("default_branch")] public string? DefaultBranch { get; init; }
        [JsonPropertyName("http_url_to_repo")] public string? HttpUrlToRepo { get; init; }
        [JsonPropertyName("visibility")] public string? Visibility { get; init; }
    }

    private sealed class GitLabMergeRequest
    {
        [JsonPropertyName("iid")] public long Iid { get; init; }
        [JsonPropertyName("web_url")] public string? WebUrl { get; init; }
    }
}
