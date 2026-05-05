using FluentAssertions;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
[Collection("CodeFlowApi")]
public sealed class HostToolsEndpointsTests
{
    private readonly CodeFlowApiFactory factory;

    public HostToolsEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Get_returns_host_tool_catalog_including_builtins()
    {
        using var client = factory.CreateClient();

        var tools = await client.GetFromJsonAsync<IReadOnlyList<HostToolDto>>("/api/host-tools");

        tools.Should().NotBeNull();
        tools!.Select(t => t.Name).Should().Contain(new[] { "echo", "now", "read_file", "apply_patch", "run_command" });

        var echo = tools.Single(t => t.Name == "echo");
        echo.Description.Should().NotBeNullOrWhiteSpace();

        tools.Single(t => t.Name == "apply_patch").IsMutating.Should().BeTrue();
        tools.Single(t => t.Name == "run_command").IsMutating.Should().BeTrue();
    }

    [Fact]
    public async Task Get_includes_container_and_web_tools_with_correct_mutating_flags()
    {
        // sc-452: the role-picker UI reads from /api/host-tools, so the catalog must surface
        // the new container.run / web_fetch / web_search entries with their authored
        // descriptions and IsMutating flags. container.run mutates the workspace via mount;
        // web_fetch / web_search are read-only.
        using var client = factory.CreateClient();

        var tools = await client.GetFromJsonAsync<IReadOnlyList<HostToolDto>>("/api/host-tools");

        tools.Should().NotBeNull();
        var names = tools!.Select(t => t.Name).ToHashSet();
        names.Should().Contain("container.run");
        names.Should().Contain("web_fetch");
        names.Should().Contain("web_search");

        var containerRun = tools.Single(t => t.Name == "container.run");
        containerRun.IsMutating.Should().BeTrue();
        containerRun.Description.Should().Contain("Docker Hub");
        containerRun.Description.Should().Contain("forbidden");

        tools.Single(t => t.Name == "web_fetch").IsMutating.Should().BeFalse();
        tools.Single(t => t.Name == "web_search").IsMutating.Should().BeFalse();
    }

    [Fact]
    public async Task Get_marks_vcs_clone_as_deprecated()
    {
        // sc-683: vcs.clone is deprecated in favor of setup_workspace. The role-editor
        // tool-picker reads from /api/host-tools and uses IsDeprecated to render a chip +
        // exclude the tool from "Select all" so admins don't pull legacy surface into a
        // fresh role.
        using var client = factory.CreateClient();

        var tools = await client.GetFromJsonAsync<IReadOnlyList<HostToolDto>>("/api/host-tools");

        tools.Should().NotBeNull();
        var clone = tools!.Single(t => t.Name == "vcs.clone");
        clone.IsDeprecated.Should().BeTrue();
        clone.Description.Should().Contain("DEPRECATED");
        clone.Description.Should().Contain("setup_workspace");

        var setup = tools.Single(t => t.Name == "setup_workspace");
        setup.IsDeprecated.Should().BeFalse(
            because: "setup_workspace is the supported replacement, not deprecated");
    }

    private sealed record HostToolDto(
        string Name,
        string Description,
        object? Parameters,
        bool IsMutating,
        bool IsDeprecated);
}
