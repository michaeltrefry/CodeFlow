using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class MeEndpointTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public MeEndpointTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Me_ReturnsCurrentUser_WithDevelopmentBypass()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<MeResponse>();
        payload.Should().NotBeNull();
        payload!.Id.Should().NotBeNullOrEmpty();
        payload.Roles.Should().Contain(role => string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record MeResponse(string Id, string? Email, string? Name, IReadOnlyList<string> Roles);
}
