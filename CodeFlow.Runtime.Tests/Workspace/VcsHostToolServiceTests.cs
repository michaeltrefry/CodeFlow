using System.Text.Json.Nodes;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class VcsHostToolServiceTests
{
    [Fact]
    public async Task OpenPullRequest_HappyPath_ReturnsUrlAndNumber()
    {
        var stub = new StubProvider
        {
            OpenPrResult = new PullRequestInfo("https://example.com/pulls/42", 42),
        };
        var service = new VcsHostToolService(new SingleProviderFactory(stub));

        var result = await service.OpenPullRequestAsync(
            new ToolCall(
                "call_pr",
                "vcs.open_pr",
                new JsonObject
                {
                    ["owner"] = "foo",
                    ["name"] = "bar",
                    ["head"] = "feat/x-3b70fc02",
                    ["base"] = "main",
                    ["title"] = "Add x",
                    ["body"] = "Closes #1",
                }),
            BuildContext("foo", "bar"));

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["url"]!.GetValue<string>().Should().Be("https://example.com/pulls/42");
        payload["number"]!.GetValue<long>().Should().Be(42);

        stub.LastOpenPrCall.Should().NotBeNull();
        stub.LastOpenPrCall!.Value.Owner.Should().Be("foo");
        stub.LastOpenPrCall.Value.Name.Should().Be("bar");
        stub.LastOpenPrCall.Value.Head.Should().Be("feat/x-3b70fc02");
        stub.LastOpenPrCall.Value.Base.Should().Be("main");
        stub.LastOpenPrCall.Value.Title.Should().Be("Add x");
        stub.LastOpenPrCall.Value.Body.Should().Be("Closes #1");
    }

    [Fact]
    public async Task OpenPullRequest_BodyIsOptional_DefaultsToEmpty()
    {
        var stub = new StubProvider
        {
            OpenPrResult = new PullRequestInfo("https://example.com/pulls/7", 7),
        };
        var service = new VcsHostToolService(new SingleProviderFactory(stub));

        await service.OpenPullRequestAsync(
            new ToolCall(
                "call_pr",
                "vcs.open_pr",
                new JsonObject
                {
                    ["owner"] = "foo",
                    ["name"] = "bar",
                    ["head"] = "feat/y",
                    ["base"] = "main",
                    ["title"] = "Add y",
                }),
            BuildContext("foo", "bar"));

        stub.LastOpenPrCall!.Value.Body.Should().Be(string.Empty);
    }

    [Fact]
    public async Task OpenPullRequest_RepoNotFound_ReturnsToolErrorWithKind()
    {
        var stub = new StubProvider
        {
            OpenPrException = new VcsRepoNotFoundException("foo", "bar"),
        };
        var service = new VcsHostToolService(new SingleProviderFactory(stub));

        var result = await service.OpenPullRequestAsync(BuildOpenPrCall(), BuildContext("foo", "bar"));

        result.IsError.Should().BeTrue();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["error"]!.GetValue<string>().Should().Be("repo_not_found");
        payload["message"]!.GetValue<string>().Should().Contain("foo/bar");
    }

    [Fact]
    public async Task OpenPullRequest_Unauthorized_ReturnsToolErrorWithKind()
    {
        var stub = new StubProvider
        {
            OpenPrException = new VcsUnauthorizedException("401 Unauthorized"),
        };
        var service = new VcsHostToolService(new SingleProviderFactory(stub));

        var result = await service.OpenPullRequestAsync(BuildOpenPrCall(), BuildContext("foo", "bar"));

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!.AsObject()["error"]!.GetValue<string>().Should().Be("unauthorized");
    }

    [Fact]
    public async Task OpenPullRequest_RateLimited_ReturnsToolErrorWithKind()
    {
        var stub = new StubProvider
        {
            OpenPrException = new VcsRateLimitedException("429 Too Many Requests"),
        };
        var service = new VcsHostToolService(new SingleProviderFactory(stub));

        var result = await service.OpenPullRequestAsync(BuildOpenPrCall(), BuildContext("foo", "bar"));

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!.AsObject()["error"]!.GetValue<string>().Should().Be("rate_limited");
    }

    [Fact]
    public async Task OpenPullRequest_NotConfigured_ReturnsToolErrorWithKind()
    {
        var service = new VcsHostToolService(new ThrowingFactory(new GitHostNotConfiguredException()));

        var result = await service.OpenPullRequestAsync(BuildOpenPrCall(), BuildContext("foo", "bar"));

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!.AsObject()["error"]!.GetValue<string>().Should().Be("not_configured");
    }

    [Fact]
    public async Task OpenPullRequest_MissingRequiredArg_Throws()
    {
        var service = new VcsHostToolService(new SingleProviderFactory(new StubProvider()));

        var act = async () => await service.OpenPullRequestAsync(
            new ToolCall(
                "call_pr",
                "vcs.open_pr",
                new JsonObject
                {
                    ["owner"] = "foo",
                    ["name"] = "bar",
                    // missing head, base, title
                }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*non-empty string 'head'*");
    }

    [Fact]
    public async Task GetRepoMetadata_HappyPath_ReturnsShape()
    {
        var stub = new StubProvider
        {
            RepoMetadata = new VcsRepoMetadata("trunk", "https://example.com/foo/bar.git", VcsRepoVisibility.Private),
        };
        var service = new VcsHostToolService(new SingleProviderFactory(stub));

        var result = await service.GetRepoMetadataAsync(
            new ToolCall(
                "call_meta",
                "vcs.get_repo",
                new JsonObject
                {
                    ["owner"] = "foo",
                    ["name"] = "bar",
                }),
            BuildContext("foo", "bar"));

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["defaultBranch"]!.GetValue<string>().Should().Be("trunk");
        payload["cloneUrl"]!.GetValue<string>().Should().Be("https://example.com/foo/bar.git");
        payload["visibility"]!.GetValue<string>().Should().Be("Private");
    }

    [Fact]
    public async Task OpenPullRequest_WhenRepoNotDeclared_ReturnsAdmissionRefusal()
    {
        // sc-272 PR2: vcs.open_pr now goes through DeliveryRequestValidator. The
        // "repo not declared" rejection rides the same refusal-payload path the rest of
        // the workspace tool refusals use, so ToolRegistry's sink picks it up as a
        // Stage = Tool RefusalEvent (vs the previous freeform error payload that was
        // invisible to the refusal-evidence stream).
        var stub = new StubProvider();
        var service = new VcsHostToolService(new SingleProviderFactory(stub));

        var result = await service.OpenPullRequestAsync(BuildOpenPrCall(), BuildContext("foo", "allowed"));

        result.IsError.Should().BeTrue();
        var refusal = JsonNode.Parse(result.Content)!["refusal"]!.AsObject();
        refusal["code"]!.GetValue<string>().Should().Be("delivery-repo-not-declared");
        refusal["axis"]!.GetValue<string>().Should().Be("delivery");
        stub.LastOpenPrCall.Should().BeNull();
    }

    [Fact]
    public async Task GetRepoMetadata_WhenLegacyWorkspaceRepoMatches_AllowsCall()
    {
        var stub = new StubProvider();
        var service = new VcsHostToolService(new SingleProviderFactory(stub));

        var result = await service.GetRepoMetadataAsync(
            new ToolCall(
                "call_meta",
                "vcs.get_repo",
                new JsonObject
                {
                    ["owner"] = "foo",
                    ["name"] = "bar",
                }),
            new ToolExecutionContext(
                new ToolWorkspaceContext(
                    Guid.NewGuid(),
                    "/tmp/work",
                    RepoUrl: "https://github.com/foo/bar.git")));

        result.IsError.Should().BeFalse();
    }

    private static ToolCall BuildOpenPrCall() =>
        new(
            "call_pr",
            "vcs.open_pr",
            new JsonObject
            {
                ["owner"] = "foo",
                ["name"] = "bar",
                ["head"] = "feat/x",
                ["base"] = "main",
                ["title"] = "Add x",
            });

    private static ToolExecutionContext BuildContext(string owner, string name) =>
        new(
            Repositories:
            [
                new ToolRepositoryContext(owner, name, $"https://example.com/{owner}/{name}.git")
            ]);

    private sealed class StubProvider : IVcsProvider
    {
        public GitHostMode Mode => GitHostMode.GitHub;
        public PullRequestInfo OpenPrResult { get; set; } = new("https://example.com/pulls/0", 0);
        public Exception? OpenPrException { get; set; }
        public VcsRepoMetadata RepoMetadata { get; set; } =
            new("main", "https://example.com/foo/bar.git", VcsRepoVisibility.Public);
        public Exception? RepoMetadataException { get; set; }

        public (string Owner, string Name, string Head, string Base, string Title, string Body)? LastOpenPrCall { get; private set; }

        public Task<VcsRepoMetadata> GetRepoMetadataAsync(string owner, string name, CancellationToken cancellationToken = default)
        {
            if (RepoMetadataException is not null) throw RepoMetadataException;
            return Task.FromResult(RepoMetadata);
        }

        public Task<PullRequestInfo> OpenPullRequestAsync(
            string owner, string name, string head, string baseRef, string title, string body,
            CancellationToken cancellationToken = default)
        {
            LastOpenPrCall = (owner, name, head, baseRef, title, body);
            if (OpenPrException is not null) throw OpenPrException;
            return Task.FromResult(OpenPrResult);
        }

        public string BuildAuthenticatedCloneUrl(string repoUrl) => repoUrl;
    }

    private sealed class SingleProviderFactory : IVcsProviderFactory
    {
        private readonly IVcsProvider provider;
        public SingleProviderFactory(IVcsProvider provider) => this.provider = provider;
        public Task<IVcsProvider> CreateAsync(CancellationToken cancellationToken = default) => Task.FromResult(provider);
    }

    private sealed class ThrowingFactory : IVcsProviderFactory
    {
        private readonly Exception ex;
        public ThrowingFactory(Exception ex) => this.ex = ex;
        public Task<IVcsProvider> CreateAsync(CancellationToken cancellationToken = default) => throw ex;
    }
}
