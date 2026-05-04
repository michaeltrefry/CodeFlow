using System.Text.Json.Nodes;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

/// <summary>
/// End-to-end coverage for epic 658 (sc-664) — verifies the full agent-style flow
/// (clone → branch → edit → commit) works through the host-tool surface and that the
/// per-trace credential-helper boundary holds. The flow uses a local file:// fixture rather
/// than a real GitHub repo so the test is hermetic and runs in CI; the no-leakage assertions
/// against a sentinel token verify the helper boundary independently of any auth roundtrip.
/// </summary>
public sealed class GitOnPathEndToEndTests : IDisposable
{
    private const string SentinelToken = "sc664-sentinel-token-do-not-leak";
    private const string SentinelHost = "sc664-fake-host.example";

    private readonly List<string> cleanupDirs = [];

    public void Dispose()
    {
        foreach (var dir in cleanupDirs)
        {
            GitTestRepo.SafeDelete(dir);
        }
    }

    [Fact]
    public async Task FullFlow_CloneCommitOnLocalRepo_NeverLeaksTheConfiguredToken()
    {
        // ---- Arrange: workspace + cred root + fixture remote ---------------------------------
        var workspaceRoot = NewTempDir("e2e-workspace-root");
        var traceWorkspaceDir = Path.Combine(workspaceRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(traceWorkspaceDir);

        var credentialRoot = NewTempDir("e2e-cred-root");
        var workspaceOptions = new WorkspaceOptions
        {
            Root = workspaceRoot,
            WorkingDirectoryRoot = workspaceRoot,
            GitCredentialRoot = credentialRoot,
            ReadMaxBytes = 64 * 1024,
            ExecTimeoutSeconds = 30,
            ExecOutputMaxBytes = 64 * 1024,
        };

        var traceId = Guid.Parse(Path.GetFileName(traceWorkspaceDir));

        // Pre-populate the per-trace cred file with a sentinel token. We then assert it never
        // surfaces in any place the agent could read — that's the load-bearing boundary
        // even when the actual auth roundtrip can't run (file:// requires no auth).
        await GitCredentialFile.WriteAsync(
            credentialRoot,
            traceId,
            new[] { new HostCredential(SentinelHost, "x-access-token", SentinelToken) });

        // Fixture upstream — a bare-init local repo to clone from. Real git host or HTTPS isn't
        // needed for the leakage assertions; sc-660/661/662 unit tests cover the helper plumbing.
        var fixtureRepo = InitFixtureRepo();
        var fixtureUrl = new Uri(fixtureRepo).AbsoluteUri;

        var workspaceContext = new ToolWorkspaceContext(
            CorrelationId: traceId,
            RootPath: traceWorkspaceDir,
            RepoUrl: fixtureUrl,
            RepoIdentityKey: null,
            RepoSlug: null);
        var toolContext = new ToolExecutionContext(workspaceContext);

        var workspaceTools = new WorkspaceHostToolService(workspaceOptions);
        var gitCli = new GitCli(workspaceOptions);

        // ---- Act 1: clone via IGitCli with the credential env (mirrors what VcsHostToolService
        //               does post sc-661). Use the clean URL — sc-662 dropped any embed step.
        var credentialEnv = GitCredentialEnv.Build(workspaceOptions.GitCredentialRoot, traceId);
        await gitCli.CloneAsync(
            fixtureUrl,
            destinationPath: Path.Combine(traceWorkspaceDir, "repo"),
            branch: null,
            depth: null,
            environmentVariables: credentialEnv);

        // ---- Act 2: branch + edit + commit via run_command, the agent's normal path. -------
        var branchResult = await workspaceTools.RunCommandAsync(
            new ToolCall("c1", "run_command", new JsonObject
            {
                ["command"] = "git",
                ["args"] = new JsonArray("checkout", "-b", "feature/sc-664-test"),
                ["workingDirectory"] = "repo",
            }),
            toolContext);
        branchResult.IsError.Should().BeFalse(branchResult.Content);

        await File.WriteAllTextAsync(
            Path.Combine(traceWorkspaceDir, "repo", "added.txt"),
            "added by sc-664 e2e\n");

        var addResult = await workspaceTools.RunCommandAsync(
            new ToolCall("c2", "run_command", new JsonObject
            {
                ["command"] = "git",
                ["args"] = new JsonArray("add", "added.txt"),
                ["workingDirectory"] = "repo",
            }),
            toolContext);
        addResult.IsError.Should().BeFalse(addResult.Content);

        // git wants user.email + user.name; provide via per-call config so we don't rely on a
        // populated global gitconfig in the test runner's home dir.
        var commitResult = await workspaceTools.RunCommandAsync(
            new ToolCall("c3", "run_command", new JsonObject
            {
                ["command"] = "git",
                ["args"] = new JsonArray(
                    "-c", "user.email=test@codeflow.local",
                    "-c", "user.name=CodeFlow Test",
                    "commit", "-m", "sc-664: e2e test commit"),
                ["workingDirectory"] = "repo",
            }),
            toolContext);
        commitResult.IsError.Should().BeFalse(commitResult.Content);

        var statusResult = await workspaceTools.RunCommandAsync(
            new ToolCall("c4", "run_command", new JsonObject
            {
                ["command"] = "git",
                ["args"] = new JsonArray("log", "-1", "--format=%s"),
                ["workingDirectory"] = "repo",
            }),
            toolContext);
        statusResult.IsError.Should().BeFalse(statusResult.Content);
        var lastSubject = JsonNode.Parse(statusResult.Content)!["stdout"]!.GetValue<string>().Trim();
        lastSubject.Should().Be("sc-664: e2e test commit");

        // ---- Assert: no token leakage anywhere the agent could observe ----------------------
        var stdoutResults = new[] { branchResult, addResult, commitResult, statusResult };
        foreach (var result in stdoutResults)
        {
            var content = result.Content;
            content.Should().NotContain(SentinelToken,
                "the configured-host token must never appear in any run_command tool result");
        }

        var gitConfigPath = Path.Combine(traceWorkspaceDir, "repo", ".git", "config");
        File.Exists(gitConfigPath).Should().BeTrue();
        var gitConfig = await File.ReadAllTextAsync(gitConfigPath);
        gitConfig.Should().NotContain(SentinelToken,
            ".git/config must never carry the auth token (sc-662 removed the embed-then-scrub flow)");
        gitConfig.Should().Contain(fixtureUrl,
            "the clean URL must round-trip into .git/config so subsequent ops use the credential helper");

        // Recursive scan of the workspace tree — every file the agent could read via
        // read_file or run_command must be free of the sentinel.
        var leaked = ScanForToken(traceWorkspaceDir, SentinelToken);
        leaked.Should().BeEmpty(
            "no file under the per-trace workspace should contain the sentinel token; " +
            "any hit means a code path leaked the cred file's contents into the agent-visible tree");

        // The cred file itself sits OUTSIDE the workspace and must contain the sentinel
        // (sanity check that the test setup is meaningful — without this, NotContain assertions
        // could pass trivially because the sentinel never made it anywhere).
        var credFilePath = GitCredentialFile.BuildPath(credentialRoot, traceId);
        var credFileContents = await File.ReadAllTextAsync(credFilePath);
        credFileContents.Should().Contain(SentinelToken,
            "test-setup sanity: the cred file must hold the sentinel for the leakage assertions to be meaningful");

        // The cred file must be UNREACHABLE via path-confined workspace tools. Issue a
        // run_command that tries to cat it via an absolute escape path; PathConfinement should
        // reject the workingDirectory before any IO happens, but even if it didn't, the cred
        // root is outside the workspace so the cat would fail anyway. We assert the structural
        // boundary by confirming the cred root is not a subpath of the workspace root.
        var workspaceFull = Path.GetFullPath(workspaceRoot);
        var credRootFull = Path.GetFullPath(credentialRoot);
        credRootFull.Should().NotStartWith(workspaceFull,
            "the cred root must never live inside WorkingDirectoryRoot — that would put it inside the agent's path-confinement boundary");
    }

    [Fact]
    public async Task PerTraceCredFile_HasMode0600_AndParentDirNotInsideWorkspaceTree()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var workspaceRoot = NewTempDir("conformance-workspace");
        var credRoot = NewTempDir("conformance-creds");
        var traceId = Guid.NewGuid();

        await GitCredentialFile.WriteAsync(
            credRoot,
            traceId,
            new[] { new HostCredential("github.com", "x-access-token", "tok") });

        var path = GitCredentialFile.BuildPath(credRoot, traceId);
        var mode = File.GetUnixFileMode(path);
        mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            "cred file mode must be 0600 — verified by sandbox-controller conformance check 10");

        Path.GetFullPath(credRoot).Should().NotStartWith(Path.GetFullPath(workspaceRoot),
            "cred root must not be a subpath of WorkingDirectoryRoot — verified by sandbox-controller conformance check 10");
    }

    private string InitFixtureRepo()
    {
        var dir = GitTestRepo.InitRepo("e2e-fixture");
        cleanupDirs.Add(dir);
        return dir;
    }

    private string NewTempDir(string prefix)
    {
        var dir = GitTestRepo.CreateTempDirectory(prefix);
        cleanupDirs.Add(dir);
        return dir;
    }

    private static List<string> ScanForToken(string root, string token)
    {
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            // .git/index is binary and may incidentally contain bytes resembling a token —
            // but our sentinel is ASCII and unique enough that a real leak would still be
            // detectable. Skip the .git/index pure-binary case so flakes don't drown the
            // signal.
            if (file.EndsWith($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}index", StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                var content = File.ReadAllText(file);
                if (content.Contains(token, StringComparison.Ordinal))
                {
                    hits.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Some files might be locked by a concurrent test; ignore.
            }
        }
        return hits;
    }
}
