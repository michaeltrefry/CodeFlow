using System.Text;
using System.Text.Json.Nodes;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class BulkReplaceHostToolTests : IDisposable
{
    private readonly string workspaceRoot;

    public BulkReplaceHostToolTests()
    {
        workspaceRoot = Path.Combine(Path.GetTempPath(), "codeflow-ws-bulk-" + Guid.NewGuid().ToString("N"));
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
    public async Task BulkReplace_LiteralAcrossManyFiles_RewritesAndCounts()
    {
        WriteFile("src/a.cs", "var MaxRoundsPerRound = 3;\nMaxRoundsPerRound++;\n");
        WriteFile("src/nested/b.cs", "// MaxRoundsPerRound is the gate\n");
        WriteFile("src/nested/c.cs", "// unrelated\n");

        var result = await NewService().BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["pattern"] = "MaxRoundsPerRound",
                ["replacement"] = "MaxStepsPerSaga",
                ["pathGlob"] = "**/*.cs"
            }),
            NewContext());

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["ok"]!.GetValue<bool>().Should().BeTrue();
        payload["filesScanned"]!.GetValue<int>().Should().Be(3);
        payload["filesChanged"]!.GetValue<int>().Should().Be(2);
        payload["totalReplacements"]!.GetValue<int>().Should().Be(3);

        (await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "src/a.cs")))
            .Should().Be("var MaxStepsPerSaga = 3;\nMaxStepsPerSaga++;\n");
        (await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "src/nested/b.cs")))
            .Should().Be("// MaxStepsPerSaga is the gate\n");
        (await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "src/nested/c.cs")))
            .Should().Be("// unrelated\n");
    }

    [Fact]
    public async Task BulkReplace_DryRun_ReturnsCountsWithoutWriting()
    {
        var (_, absA) = WriteFile("src/a.cs", "alpha alpha\n");
        WriteFile("src/b.cs", "alpha\n");

        var result = await NewService().BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["pattern"] = "alpha",
                ["replacement"] = "beta",
                ["pathGlob"] = "**/*.cs",
                ["dryRun"] = true
            }),
            NewContext());

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["dryRun"]!.GetValue<bool>().Should().BeTrue();
        payload["totalReplacements"]!.GetValue<int>().Should().Be(3);
        payload["filesChanged"]!.GetValue<int>().Should().Be(2);

        // Disk untouched.
        (await File.ReadAllTextAsync(absA)).Should().Be("alpha alpha\n");
    }

    [Fact]
    public async Task BulkReplace_RegexWithBackreference_AppliesSubstitution()
    {
        var (_, abs) = WriteFile("src/a.cs", "MaxRounds_3 MaxRounds_42\n");

        var result = await NewService().BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["pattern"] = @"MaxRounds_(\d+)",
                ["replacement"] = "MaxSteps_$1",
                ["pathGlob"] = "**/*.cs",
                ["regex"] = true
            }),
            NewContext());

        result.IsError.Should().BeFalse();
        (await File.ReadAllTextAsync(abs)).Should().Be("MaxSteps_3 MaxSteps_42\n");
    }

    [Fact]
    public async Task BulkReplace_InvalidRegex_RefusesWithPatternInvalid()
    {
        WriteFile("src/a.cs", "irrelevant\n");

        var result = await NewService().BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["pattern"] = "(unclosed",
                ["replacement"] = "x",
                ["pathGlob"] = "**/*.cs",
                ["regex"] = true
            }),
            NewContext());

        result.IsError.Should().BeTrue();
        var refusal = JsonNode.Parse(result.Content)!["refusal"]!.AsObject();
        refusal["code"]!.GetValue<string>().Should().Be("pattern-invalid");
    }

    [Fact]
    public async Task BulkReplace_NoScopeProvided_RefusesWithScopeRequired()
    {
        WriteFile("src/a.cs", "alpha\n");

        var result = await NewService().BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["pattern"] = "alpha",
                ["replacement"] = "beta"
            }),
            NewContext());

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("scope-required");
    }

    [Fact]
    public async Task BulkReplace_PathEscapesWorkspace_RefusesWithPathConfinement()
    {
        WriteFile("src/a.cs", "alpha\n");

        var result = await NewService().BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["pattern"] = "alpha",
                ["replacement"] = "beta",
                ["paths"] = new JsonArray("../etc/passwd")
            }),
            NewContext());

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("path-confinement");
    }

    [Fact]
    public async Task BulkReplace_TooManyFiles_RefusesBeforeWriting()
    {
        for (var i = 0; i < 6; i++)
        {
            WriteFile($"src/f{i}.cs", "alpha\n");
        }

        var service = NewService(maxFiles: 5);

        var result = await service.BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["pattern"] = "alpha",
                ["replacement"] = "beta",
                ["pathGlob"] = "**/*.cs"
            }),
            NewContext());

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("too_many_files");

        // Atomicity: nothing should have been rewritten.
        (await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "src/f0.cs")))
            .Should().Be("alpha\n");
    }

    [Fact]
    public async Task BulkReplace_BinaryFile_IsSkipped()
    {
        var binary = Path.Combine(workspaceRoot, "blob.bin");
        File.WriteAllBytes(binary, new byte[] { 0x41, 0x00, 0x42, 0x00, 0x43 });
        WriteFile("notes.txt", "alpha\n");

        var result = await NewService().BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["pattern"] = "alpha",
                ["replacement"] = "beta",
                ["pathGlob"] = "**/*"
            }),
            NewContext());

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        var skipped = payload["skipped"]!.AsArray();
        skipped.Select(s => s!["reason"]!.GetValue<string>()).Should().Contain("binary");
        // Binary file untouched, text file rewritten.
        File.ReadAllBytes(binary).Should().BeEquivalentTo(new byte[] { 0x41, 0x00, 0x42, 0x00, 0x43 });
        (await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "notes.txt")))
            .Should().Be("beta\n");
    }

    [Fact]
    public async Task BulkReplace_FileTooLarge_IsSkipped()
    {
        WriteFile("small.cs", "alpha\n");
        // ReadMaxBytes is set to 256 in NewService; write a file just past that.
        WriteFile("big.cs", new string('x', 300) + "alpha");

        var result = await NewService().BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["pattern"] = "alpha",
                ["replacement"] = "beta",
                ["pathGlob"] = "**/*.cs"
            }),
            NewContext());

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        var skipped = payload["skipped"]!.AsArray()
            .Select(s => s!["reason"]!.GetValue<string>())
            .ToArray();
        skipped.Should().Contain("file_too_large");
        payload["filesChanged"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task BulkReplace_ExcludedDirs_AreSkipped()
    {
        WriteFile("src/a.cs", "alpha\n");
        WriteFile("bin/Debug/copy.cs", "alpha\n");
        WriteFile("node_modules/dep/index.cs", "alpha\n");
        WriteFile(".git/HEAD/fake.cs", "alpha\n");

        var result = await NewService().BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["pattern"] = "alpha",
                ["replacement"] = "beta",
                ["pathGlob"] = "**/*.cs"
            }),
            NewContext());

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["filesChanged"]!.GetValue<int>().Should().Be(1);

        (await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "src/a.cs")))
            .Should().Be("beta\n");
        (await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "bin/Debug/copy.cs")))
            .Should().Be("alpha\n");
        (await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "node_modules/dep/index.cs")))
            .Should().Be("alpha\n");
    }

    [Fact]
    public async Task BulkReplace_GlobMatchesNothing_ReturnsZeroChanges()
    {
        WriteFile("src/a.cs", "alpha\n");

        var result = await NewService().BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["pattern"] = "alpha",
                ["replacement"] = "beta",
                ["pathGlob"] = "**/*.ts"
            }),
            NewContext());

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["ok"]!.GetValue<bool>().Should().BeTrue();
        payload["filesChanged"]!.GetValue<int>().Should().Be(0);
        payload["totalReplacements"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task BulkReplace_ExplicitPathsToFile_TargetsThatFileOnly()
    {
        WriteFile("src/a.cs", "alpha\n");
        var (rel, _) = WriteFile("src/b.cs", "alpha\n");

        var result = await NewService().BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["pattern"] = "alpha",
                ["replacement"] = "beta",
                ["paths"] = new JsonArray(rel)
            }),
            NewContext());

        result.IsError.Should().BeFalse();
        (await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "src/a.cs")))
            .Should().Be("alpha\n");
        (await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "src/b.cs")))
            .Should().Be("beta\n");
    }

    [Fact]
    public async Task BulkReplace_RequiredPatternMissing_Throws()
    {
        WriteFile("src/a.cs", "alpha\n");

        var act = async () => await NewService().BulkReplaceAsync(
            new ToolCall("c1", "bulk_replace", new JsonObject
            {
                ["replacement"] = "beta",
                ["pathGlob"] = "**/*.cs"
            }),
            NewContext());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private WorkspaceHostToolService NewService(
        WorkspaceSymlinkPolicy symlinkPolicy = WorkspaceSymlinkPolicy.RefuseForMutation,
        int maxFiles = 500)
    {
        var options = new WorkspaceOptions
        {
            Root = workspaceRoot,
            ReadMaxBytes = 256,
            ExecTimeoutSeconds = 30,
            ExecOutputMaxBytes = 64 * 1024,
            SymlinkPolicy = symlinkPolicy,
            BulkReplaceMaxFiles = maxFiles,
            BulkReplaceRegexTimeout = TimeSpan.FromSeconds(2)
        };
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
        return (relative.Replace('\\', '/'), absolute);
    }
}
