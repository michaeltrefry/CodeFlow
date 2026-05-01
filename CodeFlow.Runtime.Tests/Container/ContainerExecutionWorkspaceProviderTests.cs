using CodeFlow.Runtime.Container;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Container;

public sealed class ContainerExecutionWorkspaceProviderTests : IDisposable
{
    private readonly string canonicalRoot;
    private readonly string executionRoot;

    public ContainerExecutionWorkspaceProviderTests()
    {
        canonicalRoot = Path.Combine(Path.GetTempPath(), "codeflow-canonical-" + Guid.NewGuid().ToString("N"));
        executionRoot = Path.Combine(Path.GetTempPath(), "codeflow-exec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(canonicalRoot, "src"));
        File.WriteAllText(Path.Combine(canonicalRoot, "package.json"), "{\"name\":\"demo\"}");
        File.WriteAllText(Path.Combine(canonicalRoot, "src", "index.ts"), "console.log('hello');\n");
    }

    public void Dispose()
    {
        TryRemove(canonicalRoot);
        TryRemove(executionRoot);
    }

    [Fact]
    public void EnsureForWorkflow_creates_per_workflow_directory_under_root()
    {
        var workflowId = Guid.NewGuid();
        var provider = new ContainerExecutionWorkspaceProvider(executionRoot);

        var execPath = provider.EnsureForWorkflow(workflowId, canonicalRoot);

        execPath.Should().Be(Path.Combine(executionRoot, workflowId.ToString("N")));
        Directory.Exists(execPath).Should().BeTrue();
    }

    [Fact]
    public void EnsureForWorkflow_mirrors_canonical_workspace_contents()
    {
        var provider = new ContainerExecutionWorkspaceProvider(executionRoot);

        var execPath = provider.EnsureForWorkflow(Guid.NewGuid(), canonicalRoot);

        File.ReadAllText(Path.Combine(execPath, "package.json")).Should().Be("{\"name\":\"demo\"}");
        File.ReadAllText(Path.Combine(execPath, "src", "index.ts")).Should().Be("console.log('hello');\n");
    }

    [Fact]
    public void EnsureForWorkflow_writes_into_exec_do_not_alter_canonical()
    {
        var workflowId = Guid.NewGuid();
        var provider = new ContainerExecutionWorkspaceProvider(executionRoot);
        var execPath = provider.EnsureForWorkflow(workflowId, canonicalRoot);

        // Simulate a build container creating a new artifact and overwriting a source file.
        Directory.CreateDirectory(Path.Combine(execPath, "dist"));
        File.WriteAllText(Path.Combine(execPath, "dist", "bundle.js"), "/*compiled*/");
        File.WriteAllText(Path.Combine(execPath, "package.json"), "{\"polluted\":true}");

        File.ReadAllText(Path.Combine(canonicalRoot, "package.json")).Should().Be("{\"name\":\"demo\"}");
        File.Exists(Path.Combine(canonicalRoot, "dist", "bundle.js")).Should().BeFalse();
    }

    [Fact]
    public void EnsureForWorkflow_propagates_canonical_edits_on_subsequent_calls_but_preserves_build_artifacts()
    {
        var workflowId = Guid.NewGuid();
        var provider = new ContainerExecutionWorkspaceProvider(executionRoot);
        var execPath = provider.EnsureForWorkflow(workflowId, canonicalRoot);

        // First container.run: build artifact lands in exec.
        Directory.CreateDirectory(Path.Combine(execPath, "dist"));
        File.WriteAllText(Path.Combine(execPath, "dist", "bundle.js"), "/*compiled*/");

        // Agent edits canonical between invocations (apply_patch). Sleep so mtime is strictly newer.
        Thread.Sleep(10);
        File.WriteAllText(Path.Combine(canonicalRoot, "src", "index.ts"), "console.log('edited');\n");

        // Second container.run: re-mirror.
        var execPathAgain = provider.EnsureForWorkflow(workflowId, canonicalRoot);

        execPathAgain.Should().Be(execPath);
        File.ReadAllText(Path.Combine(execPath, "src", "index.ts")).Should().Be("console.log('edited');\n");
        File.Exists(Path.Combine(execPath, "dist", "bundle.js")).Should().BeTrue("build artifacts must survive across invocations within a workflow");
    }

    [Fact]
    public void EnsureForWorkflow_throws_when_canonical_does_not_exist()
    {
        var provider = new ContainerExecutionWorkspaceProvider(executionRoot);

        var act = () => provider.EnsureForWorkflow(Guid.NewGuid(), Path.Combine(canonicalRoot, "nope"));

        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void RemoveWorkflow_deletes_per_workflow_directory()
    {
        var workflowId = Guid.NewGuid();
        var provider = new ContainerExecutionWorkspaceProvider(executionRoot);
        var execPath = provider.EnsureForWorkflow(workflowId, canonicalRoot);

        provider.RemoveWorkflow(workflowId).Should().BeTrue();

        Directory.Exists(execPath).Should().BeFalse();
    }

    [Fact]
    public void RemoveWorkflow_returns_false_when_directory_already_absent()
    {
        var provider = new ContainerExecutionWorkspaceProvider(executionRoot);

        provider.RemoveWorkflow(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void SweepOrphans_removes_directories_older_than_max_age()
    {
        var staleId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        var provider = new ContainerExecutionWorkspaceProvider(executionRoot);
        var stalePath = provider.EnsureForWorkflow(staleId, canonicalRoot);
        var freshPath = provider.EnsureForWorkflow(freshId, canonicalRoot);

        Directory.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddHours(-30));
        Directory.SetLastWriteTimeUtc(freshPath, DateTime.UtcNow);

        var removed = provider.SweepOrphans(TimeSpan.FromHours(24), DateTimeOffset.UtcNow);

        removed.Should().Be(1);
        Directory.Exists(stalePath).Should().BeFalse();
        Directory.Exists(freshPath).Should().BeTrue();
    }

    [Fact]
    public void SweepOrphans_returns_zero_when_root_does_not_exist()
    {
        var provider = new ContainerExecutionWorkspaceProvider(
            Path.Combine(executionRoot, "no-such-dir"));

        provider.SweepOrphans(TimeSpan.FromHours(1), DateTimeOffset.UtcNow).Should().Be(0);
    }

    [Fact]
    public void Constructor_rejects_blank_execution_root()
    {
        var act = () => new ContainerExecutionWorkspaceProvider("   ");

        act.Should().Throw<ArgumentException>();
    }

    private static void TryRemove(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
