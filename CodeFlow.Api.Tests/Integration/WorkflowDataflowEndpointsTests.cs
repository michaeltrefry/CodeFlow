using CodeFlow.Persistence;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class WorkflowDataflowEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public WorkflowDataflowEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
        AgentConfigRepository.ClearCacheForTests();
    }

    [Fact]
    public async Task Get_UnknownWorkflow_Returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/workflows/df-ghost/1/dataflow");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_KnownWorkflow_ReturnsSnapshotWithPerNodeScopes()
    {
        // F2 acceptance: a 2-node workflow where the upstream agent writes `currentPlan`
        // surfaces that variable on the downstream node's scope as definite, sourced to
        // the upstream node.
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "df-architect");
        await SeedAgentAsync(client, "df-reviewer");

        var startId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var workflowKey = $"df-flow-{Guid.NewGuid():N}";

        var create = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = workflowKey,
            name = workflowKey,
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "df-architect",
                    agentVersion = (int?)1,
                    outputScript = "setWorkflow('currentPlan', input.text);",
                    outputPorts = new[] { "Continue" },
                    layoutX = 0, layoutY = 0,
                },
                new
                {
                    id = reviewerId,
                    kind = "Agent",
                    agentKey = "df-reviewer",
                    agentVersion = (int?)1,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Approved", "Rejected" },
                    layoutX = 200, layoutY = 0,
                },
            },
            edges = new object[]
            {
                new
                {
                    fromNodeId = startId,
                    fromPort = "Continue",
                    toNodeId = reviewerId,
                    toPort = "in",
                    rotatesRound = false,
                    sortOrder = 0,
                }
            },
        });
        if (!create.IsSuccessStatusCode)
        {
            var body = await create.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Workflow create failed: {create.StatusCode} {body}");
        }

        var response = await client.GetAsync($"/api/workflows/{workflowKey}/1/dataflow");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("workflowKey").GetString().Should().Be(workflowKey);
        doc.RootElement.GetProperty("workflowVersion").GetInt32().Should().Be(1);

        var scopes = doc.RootElement.GetProperty("scopesByNode");
        scopes.EnumerateObject().Count().Should().Be(2);

        var reviewerScope = scopes.GetProperty(reviewerId.ToString().ToLowerInvariant());
        var workflowVars = reviewerScope.GetProperty("workflowVariables");
        workflowVars.GetArrayLength().Should().Be(1);
        var currentPlan = workflowVars[0];
        currentPlan.GetProperty("key").GetString().Should().Be("currentPlan");
        currentPlan.GetProperty("confidence").GetString().Should().Be("Definite");

        var inputSource = reviewerScope.GetProperty("inputSource");
        inputSource.GetProperty("nodeId").GetGuid().Should().Be(startId);
        inputSource.GetProperty("port").GetString().Should().Be("Continue");
    }

    private static async Task SeedAgentAsync(HttpClient client, string key)
    {
        var response = await client.PostAsJsonAsync("/api/agents", new
        {
            key,
            config = new
            {
                provider = "openai",
                model = "gpt-5",
                systemPrompt = "Do work.",
                outputs = new object[]
                {
                    new { kind = "Continue" },
                    new { kind = "Approved" },
                    new { kind = "Rejected" },
                }
            }
        });
        response.EnsureSuccessStatusCode();
    }
}
