using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

/// <summary>
/// Coverage for the <c>setup_workspace</c> host tool (sc-680). The integration tests use a
/// real <see cref="GitCli"/> against a local file:// fixture so cloning, ls-remote, branch
/// creation, and push exercise actual git semantics — that's the whole point of moving
/// mechanical work into code, so the tests have to verify the code does it right.
///
/// Credential resolution is stubbed (we don't need a real GitHostSettings DB), and the
/// host guard is permissive in these fixtures so the focus stays on the bootstrap flow
/// itself rather than envelope policy (which has its own coverage).
/// </summary>
public sealed class SetupWorkspaceHostToolServiceTests : IDisposable
{
    private readonly List<string> cleanupDirs = [];

    public void Dispose()
    {
        foreach (var dir in cleanupDirs)
        {
            GitTestRepo.SafeDelete(dir);
        }
    }

    [Fact]
    public async Task SetupWorkspace_ClonesEachRepo_DiscoversBaseBranch_PushesFeatureBranch()
    {
        var fixture = NewBareFixture("fixture-a");
        var (service, workspaceRoot, traceId, sink) = NewService();

        var result = await CallSetupWorkspace(
            service,
            new[] { (Url: fixture.RemoteUrl, Branch: (string?)null) },
            featureBranchPrefix: null,
            workspaceRoot: workspaceRoot,
            traceId: traceId,
            sink: sink);

        result.IsError.Should().BeFalse(result.Content);
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        var repos = payload["repos"]!.AsArray();
        repos.Count.Should().Be(1);

        var repoZero = repos[0]!.AsObject();
        repoZero["url"]!.GetValue<string>().Should().Be(fixture.RemoteUrl);
        repoZero["baseBranch"]!.GetValue<string>().Should().Be("main");
        var featureBranch = repoZero["featureBranch"]!.GetValue<string>();
        featureBranch.Should().StartWith("codeflow/trace-");
        featureBranch.Should().EndWith("/fixture-a");
        repoZero["alreadyPresent"]!.GetValue<bool>().Should().BeFalse();
        repoZero["baseSha"]!.GetValue<string>().Should().MatchRegex("^[0-9a-f]{40}$");
        repoZero["localPath"]!.GetValue<string>().Should().Be("fixture-a");

        // Auth is stubbed but the cred file should still have been written.
        var credPath = GitCredentialFile.BuildPath(GetCredentialRoot(traceId), traceId);
        File.Exists(credPath).Should().BeTrue("setup_workspace must write the per-trace credential file as part of the atomic operation");

        // The feature branch must exist on the bare fixture remote — that's how we know push
        // happened. Use show-ref instead of a filesystem check because branches with slashes
        // may be stored as files (refs/heads/.../leaf) or packed into packed-refs.
        BareHasBranch(fixture.BarePath, featureBranch)
            .Should().BeTrue("setup_workspace must push the empty feature branch to the remote so auth is validated at setup time");

        // workflow.repositories must have been staged via the sink so saga.RepositoriesJson updates on submit.
        sink.StagedWrites.Should().ContainKey("repositories");
        var stagedRepos = sink.StagedWrites["repositories"];
        stagedRepos.ValueKind.Should().Be(JsonValueKind.Array);
        stagedRepos.GetArrayLength().Should().Be(1);
        stagedRepos[0].GetProperty("url").GetString().Should().Be(fixture.RemoteUrl);
        stagedRepos[0].GetProperty("branch").GetString().Should().Be("main");
    }

    [Fact]
    public async Task SetupWorkspace_IsIdempotent_AlreadyPresentRoundTripsUnchanged()
    {
        var fixture = NewBareFixture("fixture-b");
        var (service, workspaceRoot, traceId, sink) = NewService();

        var first = await CallSetupWorkspace(
            service,
            new[] { (Url: fixture.RemoteUrl, Branch: (string?)null) },
            featureBranchPrefix: null,
            workspaceRoot: workspaceRoot,
            traceId: traceId,
            sink: sink);
        first.IsError.Should().BeFalse(first.Content);
        var firstFeatureBranch = JsonNode.Parse(first.Content)!["repos"]![0]!["featureBranch"]!.GetValue<string>();

        var second = await CallSetupWorkspace(
            service,
            new[] { (Url: fixture.RemoteUrl, Branch: (string?)null) },
            featureBranchPrefix: null,
            workspaceRoot: workspaceRoot,
            traceId: traceId,
            sink: sink);
        second.IsError.Should().BeFalse(second.Content);

        var secondRepos = JsonNode.Parse(second.Content)!["repos"]!.AsArray();
        secondRepos.Count.Should().Be(1);
        secondRepos[0]!["alreadyPresent"]!.GetValue<bool>().Should().BeTrue("re-calling setup_workspace must report alreadyPresent for existing clones");
        secondRepos[0]!["featureBranch"]!.GetValue<string>().Should().Be(firstFeatureBranch, "the synthesized feature-branch name must be deterministic per traceId");
    }

    [Fact]
    public async Task SetupWorkspace_MidFlowAddition_AddsNewRepoAlongsideExisting()
    {
        var fixtureA = NewBareFixture("fixture-c");
        var fixtureB = NewBareFixture("fixture-d");
        var (service, workspaceRoot, traceId, sink) = NewService();

        var first = await CallSetupWorkspace(
            service,
            new[] { (Url: fixtureA.RemoteUrl, Branch: (string?)null) },
            featureBranchPrefix: null,
            workspaceRoot: workspaceRoot,
            traceId: traceId,
            sink: sink);
        first.IsError.Should().BeFalse(first.Content);

        // Simulate the architect-discovered-additional-repo flow: call setup_workspace
        // again with the ORIGINAL URL plus the new one. Existing repo round-trips, new
        // one goes through full setup.
        var second = await CallSetupWorkspace(
            service,
            new[]
            {
                (Url: fixtureA.RemoteUrl, Branch: (string?)null),
                (Url: fixtureB.RemoteUrl, Branch: (string?)null),
            },
            featureBranchPrefix: null,
            workspaceRoot: workspaceRoot,
            traceId: traceId,
            sink: sink);
        second.IsError.Should().BeFalse(second.Content);

        var repos = JsonNode.Parse(second.Content)!["repos"]!.AsArray();
        repos.Count.Should().Be(2);

        var byUrl = repos.ToDictionary(r => r!["url"]!.GetValue<string>());
        byUrl[fixtureA.RemoteUrl]!["alreadyPresent"]!.GetValue<bool>().Should().BeTrue();
        byUrl[fixtureB.RemoteUrl]!["alreadyPresent"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task SetupWorkspace_BaseBranchMismatch_ReturnsStructuredError_AndNoBranchIsPushed()
    {
        var fixture = NewBareFixture("fixture-e");
        var (service, workspaceRoot, traceId, sink) = NewService();

        var result = await CallSetupWorkspace(
            service,
            new[] { (Url: fixture.RemoteUrl, Branch: (string?)"master") }, // remote is on `main`
            featureBranchPrefix: null,
            workspaceRoot: workspaceRoot,
            traceId: traceId,
            sink: sink);

        result.IsError.Should().BeTrue();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["error"]!.GetValue<string>().Should().Be("base_branch_mismatch");
        payload["expected"]!.GetValue<string>().Should().Be("master");
        payload["actual"]!.GetValue<string>().Should().Be("main");
        payload["url"]!.GetValue<string>().Should().Be(fixture.RemoteUrl);

        // The base_branch_mismatch fires AFTER clone (we need a clone to run ls-remote against),
        // so the clone exists on disk — but no feature branch should have been pushed to remote.
        ListBareBranches(fixture.BarePath)
            .Should().BeEquivalentTo(new[] { "main" },
                "a base_branch_mismatch must not push a partially-set-up feature branch");

        sink.StagedWrites.Should().NotContainKey("repositories", "errors must not stage workflow.repositories writes");
    }

    [Fact]
    public async Task SetupWorkspace_NoCredentials_ReturnsAuthUnavailable()
    {
        var fixture = NewBareFixture("fixture-f");
        var (service, workspaceRoot, traceId, sink) = NewService(creds: Array.Empty<HostCredential>());

        var result = await CallSetupWorkspace(
            service,
            new[] { (Url: fixture.RemoteUrl, Branch: (string?)null) },
            featureBranchPrefix: null,
            workspaceRoot: workspaceRoot,
            traceId: traceId,
            sink: sink);

        result.IsError.Should().BeTrue();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["error"]!.GetValue<string>().Should().Be("auth_unavailable");

        // Nothing should have been cloned — the failure is fast at the credential-resolution gate.
        Directory.Exists(Path.Combine(workspaceRoot, "fixture-f")).Should().BeFalse();
        sink.StagedWrites.Should().NotContainKey("repositories");
    }

    [Fact]
    public async Task SetupWorkspace_EmptyRepositories_ReturnsRepositoriesRequired()
    {
        var (service, workspaceRoot, traceId, sink) = NewService();

        var result = await CallSetupWorkspace(
            service,
            Array.Empty<(string Url, string? Branch)>(),
            featureBranchPrefix: null,
            workspaceRoot: workspaceRoot,
            traceId: traceId,
            sink: sink);

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["error"]!.GetValue<string>().Should().Be("repositories_required");
    }

    [Fact]
    public async Task SetupWorkspace_NoWorkspaceContext_ReturnsWorkspaceRequired()
    {
        var (service, _, _, _) = NewService();
        var args = new JsonObject
        {
            ["repositories"] = new JsonArray(new JsonObject { ["url"] = "https://example.invalid/owner/repo" }),
        };

        var result = await service.SetupWorkspaceAsync(
            new ToolCall("c1", "setup_workspace", args),
            context: null);

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["error"]!.GetValue<string>().Should().Be("workspace_required");
    }

    [Fact]
    public async Task SetupWorkspace_FeatureBranchPrefix_OverridesDefault()
    {
        var fixture = NewBareFixture("fixture-g");
        var (service, workspaceRoot, traceId, sink) = NewService();

        var result = await CallSetupWorkspace(
            service,
            new[] { (Url: fixture.RemoteUrl, Branch: (string?)null) },
            featureBranchPrefix: "story/sc-1234",
            workspaceRoot: workspaceRoot,
            traceId: traceId,
            sink: sink);

        result.IsError.Should().BeFalse(result.Content);
        var featureBranch = JsonNode.Parse(result.Content)!["repos"]![0]!["featureBranch"]!.GetValue<string>();
        featureBranch.Should().Be("story/sc-1234/fixture-g");
    }

    // === fixtures ==========================================================================

    private (SetupWorkspaceHostToolService Service, string WorkspaceRoot, Guid TraceId, RecordingSink Sink) NewService(
        IReadOnlyList<HostCredential>? creds = null)
    {
        var workspaceRoot = NewTempDir("sw-workspace");
        var credentialRoot = NewTempDir("sw-creds");
        var traceId = Guid.NewGuid();

        var defaultCreds = creds ?? new[]
        {
            new HostCredential("local", "x-access-token", "stub-token-abc"),
        };

        var resolver = new StubResolver(defaultCreds);
        var options = new WorkspaceOptions
        {
            Root = workspaceRoot,
            WorkingDirectoryRoot = workspaceRoot,
            GitCredentialRoot = credentialRoot,
            GitCommandTimeout = TimeSpan.FromMinutes(1),
        };
        var gitCli = new GitCli(options);
        var service = new SetupWorkspaceHostToolService(resolver, gitCli, options, hostGuard: null);

        return (service, workspaceRoot, traceId, new RecordingSink());
    }

    private string GetCredentialRoot(Guid _) =>
        // A bit of indirection because each call to NewService creates its own root and we
        // want the helper's path to match the test's. We re-derive from the service's options
        // by exposing the value from NewService — but to keep tests terse the helper just
        // returns a path relative to the temp tree. The actual cred file path is asserted
        // via GitCredentialFile.BuildPath in the calling test, which is correct so long as
        // it uses the same root the service does. The trace-scope-resolution lives in the
        // tests below that need it.
        cleanupDirs.First(d => d.Contains("sw-creds"));

    private async Task<ToolResult> CallSetupWorkspace(
        SetupWorkspaceHostToolService service,
        IEnumerable<(string Url, string? Branch)> repos,
        string? featureBranchPrefix,
        string workspaceRoot,
        Guid traceId,
        RecordingSink sink)
    {
        var repoArray = new JsonArray();
        foreach (var (url, branch) in repos)
        {
            var repoObj = new JsonObject { ["url"] = url };
            if (!string.IsNullOrWhiteSpace(branch))
            {
                repoObj["branch"] = branch;
            }
            repoArray.Add(repoObj);
        }

        var args = new JsonObject
        {
            ["repositories"] = repoArray,
        };
        if (!string.IsNullOrWhiteSpace(featureBranchPrefix))
        {
            args["featureBranchPrefix"] = featureBranchPrefix;
        }

        var workspace = new ToolWorkspaceContext(traceId, workspaceRoot);
        var context = new ToolExecutionContext(Workspace: workspace)
        {
            StageWorkflowBagWrite = sink.Stage,
        };

        return await service.SetupWorkspaceAsync(new ToolCall("c1", "setup_workspace", args), context);
    }

    private string NewTempDir(string prefix)
    {
        var dir = GitTestRepo.CreateTempDirectory(prefix);
        cleanupDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Materializes a bare git repository on local disk + a working clone with a single
    /// commit on `main`. Returns a `RemoteUrl` shaped as `file://...` that the service
    /// passes to GitCli.CloneAsync — so we exercise real cloning without network deps.
    /// </summary>
    private (string RemoteUrl, string BarePath) NewBareFixture(string slug)
    {
        // 1) Create a working repo with a single commit on `main`.
        var seed = NewTempDir($"sw-seed-{slug}");
        GitTestRepo.RunGit(seed, "init", "-b", "main");
        GitTestRepo.RunGit(seed, "config", "user.email", "test@codeflow.local");
        GitTestRepo.RunGit(seed, "config", "user.name", "CodeFlow Test");
        File.WriteAllText(Path.Combine(seed, "README.md"), $"# {slug}\n");
        GitTestRepo.RunGit(seed, "add", "README.md");
        GitTestRepo.RunGit(seed, "commit", "-m", "init");

        // 2) Clone --bare from it so we have a remote to clone-and-push against.
        var bareParent = NewTempDir($"sw-bare-{slug}");
        var bare = Path.Combine(bareParent, $"{slug}.git");
        GitTestRepo.RunGit(bareParent, "clone", "--bare", seed, $"{slug}.git");

        // 3) Set HEAD on the bare so ls-remote --symref returns `ref: refs/heads/main`.
        GitTestRepo.RunGit(bare, "symbolic-ref", "HEAD", "refs/heads/main");

        // 4) The remote URL is a file:// URI that points at the bare. RepoReference.Parse
        //    treats file:// URIs specially — owner = parent path, name = bare-dir-without-.git.
        //    The slug constraint matters because the service uses repo.Name as the local dir.
        var url = new Uri(bare).AbsoluteUri;
        return (url, bare);
    }

    private static bool BareHasBranch(string barePath, string branch)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--git-dir");
        startInfo.ArgumentList.Add(barePath);
        startInfo.ArgumentList.Add("show-ref");
        startInfo.ArgumentList.Add("--verify");
        startInfo.ArgumentList.Add("--quiet");
        startInfo.ArgumentList.Add($"refs/heads/{branch}");
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit(15_000);
        return process.ExitCode == 0;
    }

    private static IReadOnlyList<string> ListBareBranches(string barePath)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--git-dir");
        startInfo.ArgumentList.Add(barePath);
        startInfo.ArgumentList.Add("for-each-ref");
        startInfo.ArgumentList.Add("--format=%(refname:short)");
        startInfo.ArgumentList.Add("refs/heads");
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private sealed class StubResolver : IPerTraceCredentialResolver
    {
        private readonly IReadOnlyList<HostCredential> creds;

        public StubResolver(IReadOnlyList<HostCredential> creds)
        {
            this.creds = creds;
        }

        public Task<IReadOnlyList<HostCredential>> ResolveAsync(IReadOnlyList<string> repoUrls, CancellationToken cancellationToken = default) =>
            Task.FromResult(creds);
    }

    private sealed class RecordingSink
    {
        public Dictionary<string, JsonElement> StagedWrites { get; } = new(StringComparer.Ordinal);

        public void Stage(string key, JsonElement value)
        {
            StagedWrites[key] = value.Clone();
        }
    }
}
