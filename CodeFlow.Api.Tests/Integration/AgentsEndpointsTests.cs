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

    [Fact]
    public async Task Retire_HidesFromListButKeepsVersionsAccessible()
    {
        using var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/agents", new
        {
            key = "retire-me",
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "Retire me." }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var retire = await client.PostAsync("/api/agents/retire-me/retire", content: null);
        retire.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<IReadOnlyList<SummaryDto>>("/api/agents");
        list.Should().NotBeNull();
        list!.Select(s => s.Key).Should().NotContain("retire-me");

        var version = await client.GetFromJsonAsync<VersionDetailDto>("/api/agents/retire-me/1");
        version.Should().NotBeNull();
        version!.IsRetired.Should().BeTrue();

        var versions = await client.GetFromJsonAsync<IReadOnlyList<VersionDto>>("/api/agents/retire-me/versions");
        versions.Should().NotBeNull();
        versions!.Should().HaveCount(1);

        var update = await client.PutAsJsonAsync("/api/agents/retire-me", new
        {
            config = new { provider = "openai", model = "gpt-5" }
        });
        update.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Retire_UnknownKey_Returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/agents/never-existed/retire", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record VersionDto(string Key, int Version, DateTime CreatedAtUtc, string? CreatedBy);

    private sealed record SummaryDto(string Key, int LatestVersion, bool IsRetired);

    private sealed record VersionDetailDto(string Key, int Version, bool IsRetired);
}
