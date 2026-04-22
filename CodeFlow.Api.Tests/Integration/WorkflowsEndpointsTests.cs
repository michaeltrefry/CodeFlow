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

    private sealed record NodePayload(
        Guid Id,
        string Kind,
        string? AgentKey,
        string? Script,
        IReadOnlyList<string> OutputPorts);

    private sealed record EdgePayload(Guid FromNodeId, string FromPort, Guid ToNodeId, string ToPort, bool RotatesRound);

    [Fact]
    public async Task Post_ThenGet_RoundTripsScriptedAgentNode()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-classifier");
        await SeedAgentAsync(client, "wf-accept-flow");
        await SeedAgentAsync(client, "wf-reject-flow");

        var startId = Guid.NewGuid();
        var acceptId = Guid.NewGuid();
        var rejectId = Guid.NewGuid();
        const string classifierScript = """
            setNodePath(input.verdict === 'ok' ? 'Accept' : 'Reject');
            """;

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "scripted-agent-round-trip",
            name = "Scripted agent round trip",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "wf-classifier",
                    agentVersion = (int?)null,
                    script = classifierScript,
                    outputPorts = new[] { "Accept", "Reject" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = acceptId,
                    kind = "Agent",
                    agentKey = "wf-accept-flow",
                    agentVersion = (int?)null,
                    script = (string?)null,
                    outputPorts = new[] { "Completed", "Failed" },
                    layoutX = 200,
                    layoutY = 0
                },
                new
                {
                    id = rejectId,
                    kind = "Agent",
                    agentKey = "wf-reject-flow",
                    agentVersion = (int?)null,
                    script = (string?)null,
                    outputPorts = new[] { "Completed", "Failed" },
                    layoutX = 200,
                    layoutY = 200
                }
            },
            edges = new object[]
            {
                new { fromNodeId = startId, fromPort = "Accept", toNodeId = acceptId, toPort = "in", rotatesRound = false, sortOrder = 0 },
                new { fromNodeId = startId, fromPort = "Reject", toNodeId = rejectId, toPort = "in", rotatesRound = false, sortOrder = 1 }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var detail = await client.GetFromJsonAsync<WorkflowDetailPayload>("/api/workflows/scripted-agent-round-trip");
        detail.Should().NotBeNull();
        var reloadedStart = detail!.Nodes.Single(n => n.Id == startId);
        reloadedStart.Script.Should().Be(classifierScript);
        reloadedStart.OutputPorts.Should().Equal("Accept", "Reject");

        var reloadedAccept = detail.Nodes.Single(n => n.Id == acceptId);
        reloadedAccept.Script.Should().BeNull();
        reloadedAccept.OutputPorts.Should().Equal("Completed", "Failed");
    }

    [Fact]
    public async Task ValidateScript_AcceptsAgentStyleScript()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows/validate-script", new
        {
            script = """
                if (input.decision === 'Rejected') {
                    setContext('lastRejection', input.decisionPayload);
                    setNodePath('Revise');
                } else {
                    setNodePath('Accept');
                }
                """
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ValidateScriptResponseShape>();
        body.Should().NotBeNull();
        body!.Ok.Should().BeTrue();
        body.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateScript_ReportsSyntaxErrors()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows/validate-script", new
        {
            script = "setNodePath('A';"  // missing closing paren
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ValidateScriptResponseShape>();
        body.Should().NotBeNull();
        body!.Ok.Should().BeFalse();
        body.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ValidateScript_RejectsEmptyScript()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows/validate-script", new
        {
            script = "   "
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ValidateScriptResponseShape>();
        body.Should().NotBeNull();
        body!.Ok.Should().BeFalse();
        body.Errors.Should().NotBeEmpty();
    }

    private sealed record ValidateScriptResponseShape(bool Ok, IReadOnlyList<ValidateScriptErrorShape> Errors);
    private sealed record ValidateScriptErrorShape(int Line, int Column, string Message);
}
