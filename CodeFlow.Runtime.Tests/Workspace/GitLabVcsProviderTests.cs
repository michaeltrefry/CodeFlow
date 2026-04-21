using System.Net;
using System.Text;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class GitLabVcsProviderTests
{
    private const string BaseUrl = "https://gitlab.tcdevelops.com";

    [Fact]
    public async Task GetRepoMetadataAsync_returns_metadata_on_200()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.OK,
             """{"default_branch":"main","http_url_to_repo":"https://gitlab.tcdevelops.com/team/proj.git","visibility":"private"}"""));
        var provider = CreateProvider(handler);

        var metadata = await provider.GetRepoMetadataAsync("team", "proj");

        metadata.DefaultBranch.Should().Be("main");
        metadata.CloneUrl.Should().Be("https://gitlab.tcdevelops.com/team/proj.git");
        metadata.Visibility.Should().Be(VcsRepoVisibility.Private);

        handler.LastRequest!.RequestUri!.ToString().Should().EndWith("/api/v4/projects/team%2Fproj");
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("test-token");
    }

    [Fact]
    public async Task GetRepoMetadataAsync_maps_404_to_VcsRepoNotFound()
    {
        var provider = CreateProvider(new RecordingHandler((HttpStatusCode.NotFound, "{}")));

        var act = () => provider.GetRepoMetadataAsync("team", "missing");

        await act.Should().ThrowAsync<VcsRepoNotFoundException>()
            .Where(ex => ex.Name == "missing");
    }

    [Fact]
    public async Task GetRepoMetadataAsync_maps_401_to_VcsUnauthorized()
    {
        var provider = CreateProvider(new RecordingHandler((HttpStatusCode.Unauthorized, "{\"message\":\"401 Unauthorized\"}")));

        var act = () => provider.GetRepoMetadataAsync("team", "proj");

        await act.Should().ThrowAsync<VcsUnauthorizedException>();
    }

    [Fact]
    public async Task GetRepoMetadataAsync_maps_429_to_VcsRateLimited()
    {
        var provider = CreateProvider(new RecordingHandler((HttpStatusCode.TooManyRequests, "")));

        var act = () => provider.GetRepoMetadataAsync("team", "proj");

        await act.Should().ThrowAsync<VcsRateLimitedException>();
    }

    [Fact]
    public async Task OpenPullRequestAsync_posts_to_merge_requests_and_returns_iid_and_url()
    {
        var handler = new RecordingHandler((HttpStatusCode.Created,
            """{"iid":42,"web_url":"https://gitlab.tcdevelops.com/team/proj/-/merge_requests/42"}"""));
        var provider = CreateProvider(handler);

        var pr = await provider.OpenPullRequestAsync(
            "team", "proj",
            head: "feature/x",
            baseRef: "main",
            title: "My change",
            body: "Description");

        pr.Number.Should().Be(42);
        pr.Url.Should().EndWith("/merge_requests/42");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString()
            .Should().EndWith("/api/v4/projects/team%2Fproj/merge_requests");

        var body = await handler.LastRequest.Content!.ReadAsStringAsync();
        body.Should().Contain("feature/x");
        body.Should().Contain("main");
        body.Should().Contain("My change");
    }

    [Fact]
    public async Task OpenPullRequestAsync_maps_409_to_VcsConflict()
    {
        var provider = CreateProvider(new RecordingHandler((HttpStatusCode.Conflict, "")));

        var act = () => provider.OpenPullRequestAsync("team", "proj", "h", "main", "t", "b");

        await act.Should().ThrowAsync<VcsConflictException>();
    }

    [Fact]
    public async Task ResolveBaseUri_throws_when_mode_is_github()
    {
        var provider = CreateProviderWithSettings(
            new RecordingHandler((HttpStatusCode.OK, "{}")),
            new GitHostSettings(
                Mode: GitHostMode.GitHub,
                BaseUrl: null,
                HasToken: true,
                LastVerifiedAtUtc: null,
                UpdatedBy: null,
                UpdatedAtUtc: DateTime.UtcNow));

        var act = () => provider.GetRepoMetadataAsync("team", "proj");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ResolveBaseUri_throws_when_no_settings()
    {
        var provider = CreateProviderWithSettings(
            new RecordingHandler((HttpStatusCode.OK, "{}")),
            settings: null);

        var act = () => provider.GetRepoMetadataAsync("team", "proj");

        await act.Should().ThrowAsync<GitHostNotConfiguredException>();
    }

    [Fact]
    public async Task ResolveBaseUri_throws_when_baseUrl_missing()
    {
        var provider = CreateProviderWithSettings(
            new RecordingHandler((HttpStatusCode.OK, "{}")),
            new GitHostSettings(
                Mode: GitHostMode.GitLab,
                BaseUrl: null,
                HasToken: true,
                LastVerifiedAtUtc: null,
                UpdatedBy: null,
                UpdatedAtUtc: DateTime.UtcNow));

        var act = () => provider.GetRepoMetadataAsync("team", "proj");

        await act.Should().ThrowAsync<GitHostNotConfiguredException>();
    }

    private static GitLabVcsProvider CreateProvider(RecordingHandler handler)
        => CreateProviderWithSettings(handler, DefaultSettings());

    private static GitLabVcsProvider CreateProviderWithSettings(
        RecordingHandler handler,
        GitHostSettings? settings)
    {
        var httpClient = new HttpClient(handler);
        var tokenProvider = new StaticTokenProvider("test-token");
        var reader = new StaticSettingsReader(settings);
        return new GitLabVcsProvider(httpClient, tokenProvider, reader);
    }

    private static GitHostSettings DefaultSettings() => new(
        Mode: GitHostMode.GitLab,
        BaseUrl: BaseUrl,
        HasToken: true,
        LastVerifiedAtUtc: null,
        UpdatedBy: "admin",
        UpdatedAtUtc: DateTime.UtcNow);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> responses;

        public RecordingHandler(params (HttpStatusCode, string)[] responses)
        {
            this.responses = new Queue<(HttpStatusCode, string)>(responses);
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                var clone = new HttpRequestMessage(request.Method, request.RequestUri);
                clone.Content = new StringContent(body, Encoding.UTF8, "application/json");
                foreach (var header in request.Headers)
                {
                    clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                LastRequest = clone;
            }

            var (status, content) = responses.Count > 0 ? responses.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StaticTokenProvider : IGitHostTokenProvider
    {
        private readonly string token;
        public StaticTokenProvider(string token) { this.token = token; }
        public Task<GitHostTokenLease> AcquireAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GitHostTokenLease(token));
    }

    private sealed class StaticSettingsReader : IGitHostSettingsReader
    {
        private readonly GitHostSettings? settings;
        public StaticSettingsReader(GitHostSettings? settings) { this.settings = settings; }
        public Task<GitHostSettings?> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(settings);
    }
}
