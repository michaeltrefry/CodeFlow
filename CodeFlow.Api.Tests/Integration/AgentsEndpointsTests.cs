using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class AgentsEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public AgentsEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task PostThenPut_CreatesNewVersion()
    {
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/agents", new
        {
            key = "reviewer-v1",
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "Review the draft." }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var update = await client.PutAsJsonAsync("/api/agents/reviewer-v1", new
        {
            config = new { provider = "openai", model = "gpt-5.4", systemPrompt = "Review more carefully." }
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var versions = await client.GetFromJsonAsync<IReadOnlyList<VersionDto>>("/api/agents/reviewer-v1/versions");
        versions.Should().NotBeNull();
        versions!.Should().HaveCount(2);
        versions.Select(v => v.Version).Should().BeEquivalentTo(new[] { 2, 1 }, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Post_InvalidConfig_ReturnsValidationProblem()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/agents", new
        {
            key = "bad-agent",
            config = new { provider = "banana", model = "x" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_UnknownKey_Returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/agents/never-created", new
        {
            config = new { provider = "openai", model = "gpt-5" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record VersionDto(string Key, int Version, DateTime CreatedAtUtc, string? CreatedBy);
}
