using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class WorkflowsEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public WorkflowsEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Post_Rejects_WhenReferencedAgentsMissing()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "no-such-agents",
            name = "Bad workflow",
            startAgentKey = "ghost",
            maxRoundsPerRound = 3,
            edges = new[]
            {
                new { fromAgentKey = "ghost", decision = "Completed", toAgentKey = "other", rotatesRound = false }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_ThenGet_RoundTripsWorkflow()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-writer");
        await SeedAgentAsync(client, "wf-reviewer");

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "review-flow",
            name = "Review flow",
            startAgentKey = "wf-writer",
            maxRoundsPerRound = 3,
            edges = new[]
            {
                new { fromAgentKey = "wf-writer", decision = "Completed", toAgentKey = "wf-reviewer", rotatesRound = false }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var detail = await client.GetFromJsonAsync<WorkflowDetailPayload>("/api/workflows/review-flow");
        detail.Should().NotBeNull();
        detail!.Edges.Should().ContainSingle();
    }

    private static async Task SeedAgentAsync(HttpClient client, string key)
    {
        var response = await client.PostAsJsonAsync("/api/agents", new
        {
            key,
            config = new { provider = "openai", model = "gpt-5", systemPrompt = "Do work." }
        });
        response.EnsureSuccessStatusCode();
    }

    private sealed record WorkflowDetailPayload(string Key, int Version, string Name, IReadOnlyList<EdgePayload> Edges);

    private sealed record EdgePayload(string FromAgentKey, string ToAgentKey, string Decision, bool RotatesRound);
}
