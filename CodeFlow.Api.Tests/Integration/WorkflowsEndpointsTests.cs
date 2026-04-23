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

    [Fact]
    public async Task Post_Rejects_WhenSubflowKeyIsMissing()
    {
        // S10: Subflow node must reference a SubflowKey.
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-kickoff");

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "bad-subflow-missing-key",
            name = "No subflow key",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    kind = "Start",
                    agentKey = "wf-kickoff",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = Guid.NewGuid(),
                    kind = "Subflow",
                    agentKey = (string?)null,
                    outputPorts = new[] { "Completed", "Failed", "Escalated" },
                    layoutX = 250,
                    layoutY = 0,
                    subflowKey = (string?)null,
                    subflowVersion = (int?)null
                }
            },
            edges = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_Rejects_WhenSubflowReferencesUnknownWorkflow()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-kickoff-unknown");

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "bad-subflow-unknown",
            name = "Unknown subflow",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    kind = "Start",
                    agentKey = "wf-kickoff-unknown",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = Guid.NewGuid(),
                    kind = "Subflow",
                    agentKey = (string?)null,
                    outputPorts = new[] { "Completed", "Failed", "Escalated" },
                    layoutX = 250,
                    layoutY = 0,
                    subflowKey = "does-not-exist",
                    subflowVersion = (int?)null
                }
            },
            edges = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_Rejects_WhenSubflowPinsMissingVersion()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-kickoff-pinned");
        await SeedAgentAsync(client, "wf-child-start");

        // Create a child workflow at v1 so the key exists but v99 doesn't.
        var childStartId = Guid.NewGuid();
        var childResponse = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "child-for-pinning",
            name = "Child",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = childStartId,
                    kind = "Start",
                    agentKey = "wf-child-start",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        childResponse.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "bad-subflow-pinned-missing",
            name = "Pinned missing version",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    kind = "Start",
                    agentKey = "wf-kickoff-pinned",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = Guid.NewGuid(),
                    kind = "Subflow",
                    agentKey = (string?)null,
                    outputPorts = new[] { "Completed", "Failed", "Escalated" },
                    layoutX = 250,
                    layoutY = 0,
                    subflowKey = "child-for-pinning",
                    subflowVersion = 99
                }
            },
            edges = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_Rejects_WhenSubflowReferencesItself()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-kickoff-selfref");

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "self-ref-flow",
            name = "Self-referential",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    kind = "Start",
                    agentKey = "wf-kickoff-selfref",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = Guid.NewGuid(),
                    kind = "Subflow",
                    agentKey = (string?)null,
                    outputPorts = new[] { "Completed", "Failed", "Escalated" },
                    layoutX = 250,
                    layoutY = 0,
                    subflowKey = "self-ref-flow",
                    subflowVersion = (int?)null
                }
            },
            edges = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_Rejects_WhenSubflowEdgeUsesInvalidPortName()
    {
        // The only runtime ports a Subflow node can emit are Completed / Failed / Escalated.
        // Saving a parent that wires an edge from a Subflow on any other port would never
        // match at runtime, so the save must be rejected.
        using var client = testclient();
        HttpClient testclient() => factory.CreateClient();

        await SeedAgentAsync(client, "wf-parent-invalid-port");
        await SeedAgentAsync(client, "wf-child-invalid-port-start");
        await SeedAgentAsync(client, "wf-parent-invalid-port-downstream");

        var childStartId = Guid.NewGuid();
        var childResponse = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "child-for-invalid-port",
            name = "Child",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = childStartId,
                    kind = "Start",
                    agentKey = "wf-child-invalid-port-start",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        childResponse.EnsureSuccessStatusCode();

        var parentStartId = Guid.NewGuid();
        var subflowNodeId = Guid.NewGuid();
        var downstreamId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "parent-invalid-subflow-port",
            name = "Invalid subflow port",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = parentStartId,
                    kind = "Start",
                    agentKey = "wf-parent-invalid-port",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = subflowNodeId,
                    kind = "Subflow",
                    agentKey = (string?)null,
                    outputPorts = new[] { "Completed", "Failed", "Escalated" },
                    layoutX = 250,
                    layoutY = 0,
                    subflowKey = "child-for-invalid-port",
                    subflowVersion = 1
                },
                new
                {
                    id = downstreamId,
                    kind = "Agent",
                    agentKey = "wf-parent-invalid-port-downstream",
                    outputPorts = new[] { "Completed" },
                    layoutX = 500,
                    layoutY = 0
                }
            },
            edges = new object[]
            {
                new { fromNodeId = parentStartId, fromPort = "Completed", toNodeId = subflowNodeId, toPort = "in", rotatesRound = false },
                // "Done" is not one of the fixed Subflow ports — must be rejected.
                new { fromNodeId = subflowNodeId, fromPort = "Done", toNodeId = downstreamId, toPort = "in", rotatesRound = false }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_Accepts_ValidSubflowNodeReferencingExistingWorkflow()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-parent-start");
        await SeedAgentAsync(client, "wf-valid-child-start");

        var childStartId = Guid.NewGuid();
        var childResponse = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "valid-child-flow",
            name = "Valid child",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = childStartId,
                    kind = "Start",
                    agentKey = "wf-valid-child-start",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        childResponse.EnsureSuccessStatusCode();

        var parentStartId = Guid.NewGuid();
        var subflowNodeId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "valid-parent-flow",
            name = "Valid parent",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = parentStartId,
                    kind = "Start",
                    agentKey = "wf-parent-start",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = subflowNodeId,
                    kind = "Subflow",
                    agentKey = (string?)null,
                    outputPorts = new[] { "Completed", "Failed", "Escalated" },
                    layoutX = 250,
                    layoutY = 0,
                    subflowKey = "valid-child-flow",
                    subflowVersion = 1
                }
            },
            edges = new object[]
            {
                new { fromNodeId = parentStartId, fromPort = "Completed", toNodeId = subflowNodeId, toPort = "in", rotatesRound = false }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private sealed record ValidateScriptResponseShape(bool Ok, IReadOnlyList<ValidateScriptErrorShape> Errors);
    private sealed record ValidateScriptErrorShape(int Line, int Column, string Message);
}
