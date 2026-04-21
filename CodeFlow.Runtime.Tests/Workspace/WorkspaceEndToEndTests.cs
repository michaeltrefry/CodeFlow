using System.Text.Json.Nodes;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;
using Octokit;

namespace CodeFlow.Runtime.Tests.Workspace;

[Trait("Category", "VcsIntegration")]
public sealed class WorkspaceEndToEndTests : IDisposable
{
    private readonly List<string> cleanupDirs = [];
    private readonly List<Func<Task>> cleanupTasks = [];

    public void Dispose()
    {
        foreach (var task in cleanupTasks)
        {
            try { task().GetAwaiter().GetResult(); } catch { }
        }

        foreach (var dir in cleanupDirs)
        {
            GitTestRepo.SafeDelete(dir);
        }
    }

    [Fact]
    public async Task Agent_workflow_opens_repo_edits_commits_pushes_and_opens_pr()
    {
        var env = VcsIntegrationEnv.GitHub();
        if (env is null)
        {
            return;
        }

        var root = GitTestRepo.CreateTempDirectory("codeflow-e2e-root");
        cleanupDirs.Add(root);
        Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.CacheDirectoryName));
        Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.WorkDirectoryName));

        var options = new WorkspaceOptions
        {
            Root = root,
            GitCommandTimeout = TimeSpan.FromMinutes(5),
        };
        var gitCli = new GitCli(options);
        var workspaceService = new WorkspaceService(options, gitCli);

        var tokenProvider = new StaticTokenProvider(env.Value.token);
        var vcsProvider = new GitHubVcsProvider(tokenProvider);
        var factory = new StaticFactory(vcsProvider);

        var wsProvider = new WorkspaceToolProvider(workspaceService, options);
        var vcsTool = new VcsToolProvider(workspaceService, gitCli, factory, tokenProvider);

        var ctx = new AgentInvocationContext(Guid.NewGuid());
        var repoUrl = $"https://github.com/{env.Value.owner}/{env.Value.name}.git";
        var branchName = $"codeflow/e2e-{ctx.CorrelationId.ToString("N")[..8]}";
        var fileName = $"codeflow-e2e-{ctx.CorrelationId.ToString("N")[..8]}.txt";

        var open = await wsProvider.InvokeAsync(
            new ToolCall("c1", WorkspaceToolProvider.OpenToolName, new JsonObject { ["repoUrl"] = repoUrl }),
            ctx);
        open.IsError.Should().BeFalse();
        var repoSlug = JsonNode.Parse(open.Content)!["repoSlug"]!.GetValue<string>();

        var branch = await vcsTool.InvokeAsync(
            new ToolCall("c2", VcsToolProvider.CreateBranchToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["name"] = branchName,
            }),
            ctx);
        branch.IsError.Should().BeFalse();

        var write = await wsProvider.InvokeAsync(
            new ToolCall("c3", WorkspaceToolProvider.WriteFileToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["path"] = fileName,
                ["content"] = $"codeflow e2e {DateTime.UtcNow:O}",
            }),
            ctx);
        write.IsError.Should().BeFalse();

        var commit = await vcsTool.InvokeAsync(
            new ToolCall("c4", VcsToolProvider.CommitToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["message"] = "codeflow: e2e round-trip",
            }),
            ctx);
        commit.IsError.Should().BeFalse();

        var push = await vcsTool.InvokeAsync(
            new ToolCall("c5", VcsToolProvider.PushToolName, new JsonObject { ["repoSlug"] = repoSlug }),
            ctx);
        push.IsError.Should().BeFalse();

        cleanupTasks.Add(async () =>
        {
            var client = new GitHubClient(new ProductHeaderValue("CodeFlow")) { Credentials = new Credentials(env.Value.token) };
            try
            {
                await client.Git.Reference.Delete(env.Value.owner, env.Value.name, $"heads/{branchName}");
            }
            catch { }
        });

        var pr = await vcsTool.InvokeAsync(
            new ToolCall("c6", VcsToolProvider.OpenPrToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["title"] = "CodeFlow E2E round-trip",
                ["body"] = "Opened by the C5.5 integration test.",
            }),
            ctx);
        pr.IsError.Should().BeFalse();
        var prPayload = JsonNode.Parse(pr.Content)!.AsObject();
        var prNumber = prPayload["number"]!.GetValue<int>();
        prPayload["url"]!.GetValue<string>().Should().Contain($"/pull/{prNumber}");

        cleanupTasks.Insert(0, async () =>
        {
            var client = new GitHubClient(new ProductHeaderValue("CodeFlow")) { Credentials = new Credentials(env.Value.token) };
            try
            {
                await client.PullRequest.Update(env.Value.owner, env.Value.name, prNumber,
                    new PullRequestUpdate { State = ItemState.Closed });
            }
            catch { }
        });

        var verification = new GitHubClient(new ProductHeaderValue("CodeFlow")) { Credentials = new Credentials(env.Value.token) };
        var fetchedPr = await verification.PullRequest.Get(env.Value.owner, env.Value.name, prNumber);
        fetchedPr.Head.Ref.Should().Be(branchName);
        fetchedPr.State.Value.Should().Be(ItemState.Open);
    }

    private sealed class StaticTokenProvider : IGitHostTokenProvider
    {
        private readonly string token;
        public StaticTokenProvider(string token) { this.token = token; }
        public Task<GitHostTokenLease> AcquireAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GitHostTokenLease(token));
    }

    private sealed class StaticFactory : IVcsProviderFactory
    {
        private readonly IVcsProvider provider;
        public StaticFactory(IVcsProvider provider) { this.provider = provider; }
        public Task<IVcsProvider> CreateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(provider);
    }
}
