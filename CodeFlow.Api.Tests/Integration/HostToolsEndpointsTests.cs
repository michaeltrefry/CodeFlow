using FluentAssertions;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class HostToolsEndpointsTests : IClassFixture<CodeFlowApiFactory>
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

    private sealed record HostToolDto(
        string Name,
        string Description,
        object? Parameters,
        bool IsMutating);
}
