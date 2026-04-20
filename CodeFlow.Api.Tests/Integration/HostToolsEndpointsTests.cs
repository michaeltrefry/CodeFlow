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
        tools!.Select(t => t.Name).Should().Contain(new[] { "echo", "now" });

        var echo = tools.Single(t => t.Name == "echo");
        echo.Description.Should().NotBeNullOrWhiteSpace();
    }

    private sealed record HostToolDto(
        string Name,
        string Description,
        object? Parameters,
        bool IsMutating);
}
