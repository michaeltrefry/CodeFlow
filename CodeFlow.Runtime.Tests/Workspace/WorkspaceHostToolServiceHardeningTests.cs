using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class WorkspaceHostToolServiceHardeningTests : IDisposable
{
    private readonly string workspaceRoot;

    public WorkspaceHostToolServiceHardeningTests()
    {
        workspaceRoot = Path.Combine(Path.GetTempPath(), "codeflow-ws-harden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task ApplyPatch_WithMatchingPreimage_AppliesUpdate()
    {
        var (relative, absolute) = WriteFile("src/main.txt", "alpha\nbeta\ngamma\n");
        var sha = ComputeSha256(absolute);
        var service = NewService();
        var ctx = NewContext();

        var patch = $"""
            *** Begin Patch
            *** Update File: {relative}
            *** Preimage SHA-256: {sha}
             alpha
            -beta
            +beta patched
             gamma
            *** End Patch
            """;

        var result = await service.ApplyPatchAsync(
            new ToolCall("c1", "apply_patch", new JsonObject { ["patch"] = patch }),
            ctx);

        result.IsError.Should().BeFalse();
        (await File.ReadAllTextAsync(absolute)).Should().Be("alpha\nbeta patched\ngamma\n");
    }

    [Fact]
    public async Task ApplyPatch_WithStalePreimage_RefusesWithStructuredPayload()
    {
        var (relative, _) = WriteFile("src/main.txt", "alpha\nbeta\ngamma\n");
        var service = NewService();
        var ctx = NewContext();
        var staleSha = new string('0', 64);

        var patch = $"""
            *** Begin Patch
            *** Update File: {relative}
            *** Preimage SHA-256: {staleSha}
             alpha
            -beta
            +beta patched
             gamma
            *** End Patch
            """;

        var result = await service.ApplyPatchAsync(
            new ToolCall("c1", "apply_patch", new JsonObject { ["patch"] = patch }),
            ctx);

        result.IsError.Should().BeTrue();
        var refusal = JsonNode.Parse(result.Content)!["refusal"]!.AsObject();
        refusal["code"]!.GetValue<string>().Should().Be("preimage-mismatch");
        refusal["axis"]!.GetValue<string>().Should().Be("workspace-mutation");
        refusal["path"]!.GetValue<string>().Should().Be(relative);
        refusal["detail"]!["expected"]!.GetValue<string>().Should().Be(staleSha);
    }

    [Fact]
    public async Task ApplyPatch_WithMalformedPreimage_RefusesWithStructuredPayload()
    {
        var (relative, _) = WriteFile("src/main.txt", "alpha\n");
        var service = NewService();
        var ctx = NewContext();

        var patch = $"""
            *** Begin Patch
            *** Update File: {relative}
            *** Preimage SHA-256: not-a-hash
             alpha
            +alpha plus
            *** End Patch
            """;

        var result = await service.ApplyPatchAsync(
            new ToolCall("c1", "apply_patch", new JsonObject { ["patch"] = patch }),
            ctx);

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("preimage-malformed");
    }

    [Fact]
    public async Task ApplyPatch_DeleteWithMatchingPreimage_DeletesFile()
    {
        var (relative, absolute) = WriteFile("doomed.txt", "bye\n");
        var sha = ComputeSha256(absolute);
        var service = NewService();
        var ctx = NewContext();

        var patch = $"""
            *** Begin Patch
            *** Delete File: {relative}
            *** Preimage SHA-256: {sha}
            *** End Patch
            """;

        var result = await service.ApplyPatchAsync(
            new ToolCall("c1", "apply_patch", new JsonObject { ["patch"] = patch }),
            ctx);

        result.IsError.Should().BeFalse();
        File.Exists(absolute).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyPatch_WithoutPreimage_StillSucceeds_ForBackCompat()
    {
        var (relative, absolute) = WriteFile("src/main.txt", "alpha\nbeta\ngamma\n");
        var service = NewService();
        var ctx = NewContext();

        var patch = $"""
            *** Begin Patch
            *** Update File: {relative}
             alpha
            -beta
            +beta patched
             gamma
            *** End Patch
            """;

        var result = await service.ApplyPatchAsync(
            new ToolCall("c1", "apply_patch", new JsonObject { ["patch"] = patch }),
            ctx);

        result.IsError.Should().BeFalse();
        (await File.ReadAllTextAsync(absolute)).Should().Be("alpha\nbeta patched\ngamma\n");
    }

    [Fact]
    public async Task ApplyPatch_UpdateThroughSymlink_RefusesByDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (_, target) = WriteFile("real.txt", "real content\n");
        var linkRelative = "link.txt";
        var linkAbsolute = Path.Combine(workspaceRoot, linkRelative);
        File.CreateSymbolicLink(linkAbsolute, target);

        var service = NewService();
        var ctx = NewContext();

        var patch = $"""
            *** Begin Patch
            *** Update File: {linkRelative}
            +new
            *** End Patch
            """;

        var result = await service.ApplyPatchAsync(
            new ToolCall("c1", "apply_patch", new JsonObject { ["patch"] = patch }),
            ctx);

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("symlink-refused");
    }

    [Fact]
    public async Task ApplyPatch_AddBelowSymlinkedDirectory_RefusesByDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var realDir = Path.Combine(workspaceRoot, "real-dir");
        Directory.CreateDirectory(realDir);
        var linkDir = Path.Combine(workspaceRoot, "linked");
        Directory.CreateSymbolicLink(linkDir, realDir);

        var service = NewService();
        var ctx = NewContext();

        var patch = """
            *** Begin Patch
            *** Add File: linked/new.txt
            +hello
            *** End Patch
            """;

        var result = await service.ApplyPatchAsync(
            new ToolCall("c1", "apply_patch", new JsonObject { ["patch"] = patch }),
            ctx);

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("symlink-refused");
    }

    [Fact]
    public async Task ApplyPatch_UpdateThroughSymlink_AllowedWhenPolicyIsAllowAll()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (_, target) = WriteFile("real.txt", "alpha\n");
        var linkRelative = "link.txt";
        var linkAbsolute = Path.Combine(workspaceRoot, linkRelative);
        File.CreateSymbolicLink(linkAbsolute, target);

        var service = NewService(symlinkPolicy: WorkspaceSymlinkPolicy.AllowAll);
        var ctx = NewContext();

        var patch = $"""
            *** Begin Patch
            *** Update File: {linkRelative}
             alpha
            +alpha plus
            *** End Patch
            """;

        var result = await service.ApplyPatchAsync(
            new ToolCall("c1", "apply_patch", new JsonObject { ["patch"] = patch }),
            ctx);

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyPatch_ContextMismatch_ReturnsStructuredRefusal()
    {
        var (relative, _) = WriteFile("src/main.txt", "alpha\nbeta\n");
        var service = NewService();
        var ctx = NewContext();

        var patch = $"""
            *** Begin Patch
            *** Update File: {relative}
             alpha
            -delta
            +delta patched
             beta
            *** End Patch
            """;

        var result = await service.ApplyPatchAsync(
            new ToolCall("c1", "apply_patch", new JsonObject { ["patch"] = patch }),
            ctx);

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("context-mismatch");
    }

    [Fact]
    public async Task ApplyPatch_OutsideWorkspace_ReturnsStructuredRefusal()
    {
        var service = NewService();
        var ctx = NewContext();

        var patch = """
            *** Begin Patch
            *** Add File: ../escape.txt
            +pwned
            *** End Patch
            """;

        var result = await service.ApplyPatchAsync(
            new ToolCall("c1", "apply_patch", new JsonObject { ["patch"] = patch }),
            ctx);

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("path-confinement");
    }

    [Fact]
    public async Task RunCommand_WithEmptyAllowlist_AllowsAnyCommand()
    {
        var service = NewService(commandAllowlist: null);
        var ctx = NewContext();

        var result = await service.RunCommandAsync(
            new ToolCall("c1", "run_command", new JsonObject
            {
                ["command"] = "dotnet",
                ["args"] = new JsonArray("--version")
            }),
            ctx);

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task RunCommand_DisallowedCommand_RefusedWithStructuredPayload()
    {
        var service = NewService(commandAllowlist: new List<string> { "git", "node" });
        var ctx = NewContext();

        var result = await service.RunCommandAsync(
            new ToolCall("c1", "run_command", new JsonObject
            {
                ["command"] = "rm",
                ["args"] = new JsonArray("-rf", "/")
            }),
            ctx);

        result.IsError.Should().BeTrue();
        var refusal = JsonNode.Parse(result.Content)!["refusal"]!.AsObject();
        refusal["code"]!.GetValue<string>().Should().Be("command-allowlist");
        refusal["command"]!.GetValue<string>().Should().Be("rm");
        refusal["allowed"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().BeEquivalentTo(new[] { "git", "node" });
    }

    [Fact]
    public async Task RunCommand_AllowlistedCommand_Runs()
    {
        var service = NewService(commandAllowlist: new List<string> { "dotnet" });
        var ctx = NewContext();

        var result = await service.RunCommandAsync(
            new ToolCall("c1", "run_command", new JsonObject
            {
                ["command"] = "dotnet",
                ["args"] = new JsonArray("--version")
            }),
            ctx);

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task RunCommand_SetsGitCredentialEnvVarsOnSpawnedProcess_WhenCredentialRootConfigured()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // The test relies on `sh`, which doesn't ship the same way on Windows.
        }

        var credRoot = Path.Combine(Path.GetTempPath(), $"cred-env-test-{Guid.NewGuid():N}");
        var service = NewService(commandAllowlist: new List<string> { "sh" }, gitCredentialRoot: credRoot);
        var ctx = NewContext();

        // Use `sh -c env` so the child shell prints every env var on its own line — printenv
        // with multiple args silently skips unset ones, which masks failures here.
        var result = await service.RunCommandAsync(
            new ToolCall("c1", "run_command", new JsonObject
            {
                ["command"] = "sh",
                ["args"] = new JsonArray("-c", "env | grep ^GIT_CONFIG_ | sort"),
            }),
            ctx);

        result.IsError.Should().BeFalse();
        var stdout = JsonNode.Parse(result.Content)!["stdout"]!.GetValue<string>();
        stdout.Should().Contain("GIT_CONFIG_COUNT=2");
        stdout.Should().Contain("GIT_CONFIG_KEY_0=credential.helper");
        stdout.Should().Contain("GIT_CONFIG_VALUE_0=store --file=");
        stdout.Should().Contain($"{ctx.Workspace!.CorrelationId:N}",
            "per-trace cred file path is keyed by the trace's CorrelationId");
        stdout.Should().Contain("GIT_CONFIG_KEY_1=credential.useHttpPath");
        stdout.Should().Contain("GIT_CONFIG_VALUE_1=true");
    }

    private WorkspaceHostToolService NewService(
        IList<string>? commandAllowlist = null,
        WorkspaceSymlinkPolicy symlinkPolicy = WorkspaceSymlinkPolicy.RefuseForMutation,
        string? gitCredentialRoot = null)
    {
        var options = new WorkspaceOptions
        {
            Root = workspaceRoot,
            ReadMaxBytes = 64 * 1024,
            ExecTimeoutSeconds = 30,
            ExecOutputMaxBytes = 64 * 1024,
            CommandAllowlist = commandAllowlist,
            SymlinkPolicy = symlinkPolicy,
        };
        if (gitCredentialRoot is not null)
        {
            options.GitCredentialRoot = gitCredentialRoot;
        }
        return new WorkspaceHostToolService(options);
    }

    private ToolExecutionContext NewContext() => new(
        new ToolWorkspaceContext(
            Guid.NewGuid(),
            workspaceRoot,
            RepoUrl: "https://github.com/example/repo.git",
            RepoIdentityKey: "github.com/example/repo",
            RepoSlug: "example/repo"));

    private (string Relative, string Absolute) WriteFile(string relative, string content)
    {
        var absolute = Path.Combine(workspaceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
        return (relative, absolute);
    }

    private static string ComputeSha256(string absolutePath)
    {
        using var stream = File.OpenRead(absolutePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
