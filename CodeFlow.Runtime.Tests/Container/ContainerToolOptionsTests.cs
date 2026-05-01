using CodeFlow.Runtime.Container;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Container;

public sealed class ContainerToolOptionsTests
{
    [Fact]
    public void Defaults_match_planned_container_policy()
    {
        var options = new ContainerToolOptions();

        options.AllowedImageRegistries.Should().ContainSingle().Which.Should().Be("docker.io");
        options.AllowNetwork.Should().BeTrue();
        options.WorkspaceMountPath.Should().Be("/workspace");
        options.CpuCount.Should().Be(2);
        options.MemoryBytes.Should().Be(4L * 1024 * 1024 * 1024);
        options.PidsLimit.Should().Be(1024);
        options.CommandTimeoutSeconds.Should().Be(20 * 60);
        options.StdoutMaxBytes.Should().Be(2 * 1024 * 1024);
        options.StderrMaxBytes.Should().Be(2 * 1024 * 1024);
        options.MaxContainersPerWorkflow.Should().Be(3);
        options.MaxCacheVolumesPerWorkflow.Should().Be(5);
        options.AllowRepoDockerfiles.Should().BeFalse();
        options.AllowDockerBuild.Should().BeFalse();
        options.AllowDockerCompose.Should().BeFalse();
        options.AllowPrivileged.Should().BeFalse();
        options.AllowHostNetwork.Should().BeFalse();
        options.AllowPublishedPorts.Should().BeFalse();
        options.AllowDockerSocketMount.Should().BeFalse();
        options.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_rejects_forbidden_docker_capabilities_when_enabled()
    {
        var options = new ContainerToolOptions
        {
            AllowRepoDockerfiles = true,
            AllowDockerBuild = true,
            AllowDockerCompose = true,
            AllowPrivileged = true,
            AllowHostNetwork = true,
            AllowPublishedPorts = true,
            AllowDockerSocketMount = true
        };

        var errors = options.Validate();

        errors.Should().Contain(error => error.Contains("AllowRepoDockerfiles"));
        errors.Should().Contain(error => error.Contains("AllowDockerBuild"));
        errors.Should().Contain(error => error.Contains("AllowDockerCompose"));
        errors.Should().Contain(error => error.Contains("AllowPrivileged"));
        errors.Should().Contain(error => error.Contains("AllowHostNetwork"));
        errors.Should().Contain(error => error.Contains("AllowPublishedPorts"));
        errors.Should().Contain(error => error.Contains("AllowDockerSocketMount"));
    }

    [Fact]
    public void Validate_rejects_relative_execution_workspace_root_path()
    {
        var options = new ContainerToolOptions
        {
            ExecutionWorkspaceRootPath = "relative/path"
        };

        options.Validate().Should().Contain(error => error.Contains("ExecutionWorkspaceRootPath"));
    }

    [Fact]
    public void Validate_accepts_absolute_execution_workspace_root_path()
    {
        var absolutePath = OperatingSystem.IsWindows() ? "C:\\codeflow\\exec" : "/var/codeflow/exec";
        var options = new ContainerToolOptions
        {
            ExecutionWorkspaceRootPath = absolutePath
        };

        options.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_rejects_invalid_limits_and_paths()
    {
        var options = new ContainerToolOptions
        {
            AllowedImageRegistries = [],
            WorkspaceMountPath = "workspace",
            ExecutionWorkspaceDirectoryName = "nested/workspace",
            CpuCount = 0,
            MemoryBytes = 0,
            PidsLimit = 0,
            CommandTimeoutSeconds = 0,
            ImagePullTimeoutSeconds = 0,
            StdoutMaxBytes = 0,
            StderrMaxBytes = 0,
            MaxContainersPerWorkflow = 0,
            MaxCacheVolumesPerWorkflow = -1,
            OrphanCleanupTtl = TimeSpan.Zero
        };

        var errors = options.Validate();

        errors.Should().Contain(error => error.Contains("AllowedImageRegistries"));
        errors.Should().Contain(error => error.Contains("WorkspaceMountPath"));
        errors.Should().Contain(error => error.Contains("ExecutionWorkspaceDirectoryName"));
        errors.Should().Contain(error => error.Contains("CpuCount"));
        errors.Should().Contain(error => error.Contains("MemoryBytes"));
        errors.Should().Contain(error => error.Contains("PidsLimit"));
        errors.Should().Contain(error => error.Contains("CommandTimeoutSeconds"));
        errors.Should().Contain(error => error.Contains("ImagePullTimeoutSeconds"));
        errors.Should().Contain(error => error.Contains("StdoutMaxBytes"));
        errors.Should().Contain(error => error.Contains("StderrMaxBytes"));
        errors.Should().Contain(error => error.Contains("MaxContainersPerWorkflow"));
        errors.Should().Contain(error => error.Contains("MaxCacheVolumesPerWorkflow"));
        errors.Should().Contain(error => error.Contains("OrphanCleanupTtl"));
    }
}
