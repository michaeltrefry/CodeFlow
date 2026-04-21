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

        var startId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "no-such-agents",
            name = "Bad workflow",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "ghost",
                    agentVersion = (int?)null,
                    script = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_ThenGet_RoundTripsWorkflow()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-writer");
        await SeedAgentAsync(client, "wf-reviewer");

        var startId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "review-flow",
            name = "Review flow",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "wf-writer",
                    agentVersion = (int?)null,
                    script = (string?)null,
                    outputPorts = new[] { "Completed", "Approved", "Rejected", "Failed" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = reviewerId,
                    kind = "Agent",
                    agentKey = "wf-reviewer",
                    agentVersion = (int?)null,
                    script = (string?)null,
                    outputPorts = new[] { "Completed", "Approved", "Rejected", "Failed" },
                    layoutX = 200,
                    layoutY = 0
                }
            },
            edges = new object[]
            {
                new
                {
                    fromNodeId = startId,
                    fromPort = "Completed",
                    toNodeId = reviewerId,
                    toPort = "in",
                    rotatesRound = false,
                    sortOrder = 0
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var detail = await client.GetFromJsonAsync<WorkflowDetailPayload>("/api/workflows/review-flow");
        detail.Should().NotBeNull();
        detail!.Nodes.Should().HaveCount(2);
        detail.Edges.Should().ContainSingle()
            .Which.FromPort.Should().Be("Completed");
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

    private sealed record WorkflowDetailPayload(
        string Key,
        int Version,
        string Name,
        IReadOnlyList<NodePayload> Nodes,
        IReadOnlyList<EdgePayload> Edges);

    private sealed record NodePayload(Guid Id, string Kind, string? AgentKey);

    private sealed record EdgePayload(Guid FromNodeId, string FromPort, Guid ToNodeId, string ToPort, bool RotatesRound);
}
