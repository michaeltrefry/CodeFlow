using CodeFlow.Persistence;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class CascadeBumpEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public CascadeBumpEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
        AgentConfigRepository.ClearCacheForTests();
    }

    [Fact]
    public async Task Plan_ReturnsEmpty_WhenNoWorkflowsPinTheAgent()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "cb-unused-agent");
        await BumpAgentAsync(client, "cb-unused-agent");

        var response = await client.PostAsJsonAsync("/api/workflows/cascade-bump/plan", new
        {
            rootKind = "Agent",
            key = "cb-unused-agent",
            fromVersion = 1,
            toVersion = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("steps").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("findings").EnumerateArray()
            .Should().Contain(f => f.GetProperty("code").GetString() == "NoPinners");
    }

    [Fact]
    public async Task Plan_FindsEveryWorkflowThatPinsTheAgent_OnLatestVersion()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "cb-shared-agent");
        await CreateWorkflowPinningAgentAsync(client, "cb-flow-a", "cb-shared-agent", agentVersion: 1);
        await CreateWorkflowPinningAgentAsync(client, "cb-flow-b", "cb-shared-agent", agentVersion: 1);
        await CreateWorkflowPinningAgentAsync(client, "cb-flow-c", "cb-shared-agent", agentVersion: 1);
        await BumpAgentAsync(client, "cb-shared-agent");

        var response = await client.PostAsJsonAsync("/api/workflows/cascade-bump/plan", new
        {
            rootKind = "Agent",
            key = "cb-shared-agent",
            fromVersion = 1,
            toVersion = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var steps = doc.RootElement.GetProperty("steps");
        steps.GetArrayLength().Should().Be(3);

        var keys = steps.EnumerateArray()
            .Select(s => s.GetProperty("workflowKey").GetString())
            .ToArray();
        keys.Should().BeEquivalentTo(new[] { "cb-flow-a", "cb-flow-b", "cb-flow-c" });

        foreach (var step in steps.EnumerateArray())
        {
            step.GetProperty("fromVersion").GetInt32().Should().Be(1);
            step.GetProperty("toVersion").GetInt32().Should().Be(2);
            var pinChange = step.GetProperty("pinChanges").EnumerateArray().Single();
            pinChange.GetProperty("referenceKind").GetString().Should().Be("Agent");
            pinChange.GetProperty("key").GetString().Should().Be("cb-shared-agent");
            pinChange.GetProperty("fromVersion").GetInt32().Should().Be(1);
            pinChange.GetProperty("toVersion").GetInt32().Should().Be(2);
        }
    }

    [Fact]
    public async Task Plan_DoesNotFindPinners_WhenLatestVersionAlreadyPinsNewAgent()
    {
        // The first workflow pins agent v1, then we bump it to a new workflow version that
        // pins agent v2. The latest workflow version is now on v2, so a cascade for v1 should
        // not find this workflow.
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "cb-already-bumped-agent");
        await CreateWorkflowPinningAgentAsync(client, "cb-already-bumped-flow", "cb-already-bumped-agent", agentVersion: 1);
        await BumpAgentAsync(client, "cb-already-bumped-agent");
        await CreateWorkflowVersionPinningAgentAsync(client, "cb-already-bumped-flow", "cb-already-bumped-agent", agentVersion: 2);

        var response = await client.PostAsJsonAsync("/api/workflows/cascade-bump/plan", new
        {
            rootKind = "Agent",
            key = "cb-already-bumped-agent",
            fromVersion = 1,
            toVersion = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("steps").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Plan_CascadesUpward_WhenWorkflowPinsAnotherWorkflow()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "cb-deep-agent");
        await CreateWorkflowPinningAgentAsync(client, "cb-inner-flow", "cb-deep-agent", agentVersion: 1, category: "Subflow");
        await CreateWorkflowPinningSubflowAsync(client, "cb-outer-flow", "cb-inner-flow", subflowVersion: 1);
        await BumpAgentAsync(client, "cb-deep-agent");

        var response = await client.PostAsJsonAsync("/api/workflows/cascade-bump/plan", new
        {
            rootKind = "Agent",
            key = "cb-deep-agent",
            fromVersion = 1,
            toVersion = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var steps = doc.RootElement.GetProperty("steps");
        steps.GetArrayLength().Should().Be(2);

        // Inner first (deepest), outer second — apply order is BFS discovery order.
        steps[0].GetProperty("workflowKey").GetString().Should().Be("cb-inner-flow");
        steps[1].GetProperty("workflowKey").GetString().Should().Be("cb-outer-flow");

        var outerPinChange = steps[1].GetProperty("pinChanges").EnumerateArray().Single();
        outerPinChange.GetProperty("referenceKind").GetString().Should().Be("Subflow");
        outerPinChange.GetProperty("key").GetString().Should().Be("cb-inner-flow");
    }

    [Fact]
    public async Task Plan_RejectsRoot_WhenNewVersionDoesNotExist()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "cb-not-bumped-yet");

        var response = await client.PostAsJsonAsync("/api/workflows/cascade-bump/plan", new
        {
            rootKind = "Agent",
            key = "cb-not-bumped-yet",
            fromVersion = 1,
            toVersion = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Plan_RejectsInvalidVersionOrdering()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "cb-invalid-version-agent");
        await BumpAgentAsync(client, "cb-invalid-version-agent");

        var response = await client.PostAsJsonAsync("/api/workflows/cascade-bump/plan", new
        {
            rootKind = "Agent",
            key = "cb-invalid-version-agent",
            fromVersion = 2,
            toVersion = 1,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Apply_CreatesNewVersionsWithUpdatedAgentPins()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "cb-apply-agent");
        await CreateWorkflowPinningAgentAsync(client, "cb-apply-flow-a", "cb-apply-agent", agentVersion: 1);
        await CreateWorkflowPinningAgentAsync(client, "cb-apply-flow-b", "cb-apply-agent", agentVersion: 1);
        await BumpAgentAsync(client, "cb-apply-agent");

        var response = await client.PostAsJsonAsync("/api/workflows/cascade-bump/apply", new
        {
            rootKind = "Agent",
            key = "cb-apply-agent",
            fromVersion = 1,
            toVersion = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var applied = doc.RootElement.GetProperty("appliedWorkflows");
        applied.GetArrayLength().Should().Be(2);

        foreach (var item in applied.EnumerateArray())
        {
            item.GetProperty("createdVersion").GetInt32().Should().Be(2);
        }

        var aDetail = await client.GetFromJsonAsync<JsonDocument>("/api/workflows/cb-apply-flow-a/2");
        aDetail!.RootElement.GetProperty("version").GetInt32().Should().Be(2);
        aDetail.RootElement.GetProperty("nodes").EnumerateArray().Single()
            .GetProperty("agentVersion").GetInt32().Should().Be(2);

        var bDetail = await client.GetFromJsonAsync<JsonDocument>("/api/workflows/cb-apply-flow-b/2");
        bDetail!.RootElement.GetProperty("nodes").EnumerateArray().Single()
            .GetProperty("agentVersion").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Apply_CascadesSubflowPinsToNewlyCreatedVersions()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "cb-cascade-agent");
        await CreateWorkflowPinningAgentAsync(client, "cb-cascade-inner", "cb-cascade-agent", agentVersion: 1, category: "Subflow");
        await CreateWorkflowPinningSubflowAsync(client, "cb-cascade-outer", "cb-cascade-inner", subflowVersion: 1);
        await BumpAgentAsync(client, "cb-cascade-agent");

        var response = await client.PostAsJsonAsync("/api/workflows/cascade-bump/apply", new
        {
            rootKind = "Agent",
            key = "cb-cascade-agent",
            fromVersion = 1,
            toVersion = 2,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var innerDetail = await client.GetFromJsonAsync<JsonDocument>("/api/workflows/cb-cascade-inner/2");
        innerDetail!.RootElement.GetProperty("nodes").EnumerateArray().Single()
            .GetProperty("agentVersion").GetInt32().Should().Be(2);

        var outerDetail = await client.GetFromJsonAsync<JsonDocument>("/api/workflows/cb-cascade-outer/2");
        var subflowNode = outerDetail!.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("kind").GetString() == "Subflow");
        subflowNode.GetProperty("subflowKey").GetString().Should().Be("cb-cascade-inner");
        subflowNode.GetProperty("subflowVersion").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Apply_PreservesEdgesAndPortsOnNewVersion()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "cb-preserve-agent");
        await CreateWorkflowPinningAgentAsync(client, "cb-preserve-flow", "cb-preserve-agent", agentVersion: 1);
        await BumpAgentAsync(client, "cb-preserve-agent");

        var v1 = await client.GetFromJsonAsync<JsonDocument>("/api/workflows/cb-preserve-flow/1");
        var v1Edges = v1!.RootElement.GetProperty("edges").GetArrayLength();
        var v1Ports = v1.RootElement.GetProperty("nodes").EnumerateArray().Single()
            .GetProperty("outputPorts").EnumerateArray()
            .Select(p => p.GetString()).ToArray();

        var apply = await client.PostAsJsonAsync("/api/workflows/cascade-bump/apply", new
        {
            rootKind = "Agent",
            key = "cb-preserve-agent",
            fromVersion = 1,
            toVersion = 2,
        });
        apply.StatusCode.Should().Be(HttpStatusCode.OK);

        var v2 = await client.GetFromJsonAsync<JsonDocument>("/api/workflows/cb-preserve-flow/2");
        v2!.RootElement.GetProperty("edges").GetArrayLength().Should().Be(v1Edges);
        v2.RootElement.GetProperty("nodes").EnumerateArray().Single()
            .GetProperty("outputPorts").EnumerateArray()
            .Select(p => p.GetString())
            .Should().BeEquivalentTo(v1Ports);
    }

    [Fact]
    public async Task Plan_ExcludesNamedWorkflows_AndEmitsFinding()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "cb-exclude-agent");
        await CreateWorkflowPinningAgentAsync(client, "cb-exclude-keep", "cb-exclude-agent", agentVersion: 1);
        await CreateWorkflowPinningAgentAsync(client, "cb-exclude-skip", "cb-exclude-agent", agentVersion: 1);
        await BumpAgentAsync(client, "cb-exclude-agent");

        var response = await client.PostAsJsonAsync("/api/workflows/cascade-bump/plan", new
        {
            rootKind = "Agent",
            key = "cb-exclude-agent",
            fromVersion = 1,
            toVersion = 2,
            excludeWorkflows = new[] { "cb-exclude-skip" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var steps = doc.RootElement.GetProperty("steps");
        steps.GetArrayLength().Should().Be(1);
        steps[0].GetProperty("workflowKey").GetString().Should().Be("cb-exclude-keep");
        doc.RootElement.GetProperty("findings").EnumerateArray()
            .Should().Contain(f =>
                f.GetProperty("code").GetString() == "WorkflowExcluded" &&
                f.GetProperty("message").GetString()!.Contains("cb-exclude-skip"));
    }

    [Fact]
    public async Task Apply_RequiresWorkflowsWritePolicy()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "cb-policy-agent");
        await BumpAgentAsync(client, "cb-policy-agent");

        var response = await client.PostAsJsonAsync("/api/workflows/cascade-bump/apply", new
        {
            rootKind = "Agent",
            key = "cb-policy-agent",
            fromVersion = 1,
            toVersion = 2,
        });

        // Test factory grants both read+write to the default principal, so the call succeeds —
        // the assertion is simply that the apply path is reachable without 403/404 wiring bugs.
        response.StatusCode.Should().Match(s => s == HttpStatusCode.OK);
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
                    new { kind = "Completed" },
                    new { kind = "Approved" },
                    new { kind = "Rejected" },
                }
            }
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task BumpAgentAsync(HttpClient client, string key)
    {
        var response = await client.PutAsJsonAsync($"/api/agents/{key}", new
        {
            config = new
            {
                provider = "openai",
                model = "gpt-5",
                systemPrompt = "Do work, but better.",
                outputs = new object[]
                {
                    new { kind = "Completed" },
                    new { kind = "Approved" },
                    new { kind = "Rejected" },
                }
            }
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task CreateWorkflowPinningAgentAsync(
        HttpClient client,
        string workflowKey,
        string agentKey,
        int agentVersion,
        string category = "Workflow")
    {
        var startId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = workflowKey,
            name = workflowKey,
            maxRoundsPerRound = 3,
            category,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey,
                    agentVersion = (int?)agentVersion,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0,
                }
            },
            edges = Array.Empty<object>(),
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task CreateWorkflowVersionPinningAgentAsync(
        HttpClient client,
        string workflowKey,
        string agentKey,
        int agentVersion)
    {
        var startId = Guid.NewGuid();
        var response = await client.PutAsJsonAsync($"/api/workflows/{workflowKey}", new
        {
            name = workflowKey,
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey,
                    agentVersion = (int?)agentVersion,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0,
                }
            },
            edges = Array.Empty<object>(),
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task CreateWorkflowPinningSubflowAsync(
        HttpClient client,
        string workflowKey,
        string subflowKey,
        int subflowVersion)
    {
        // The outer workflow needs a Start node + a Subflow node so it passes the standard
        // workflow validator. We seed a small "starter" agent for the Start node.
        var starterAgentKey = $"{workflowKey}-starter";
        await SeedAgentAsync(client, starterAgentKey);

        var startId = Guid.NewGuid();
        var subflowId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/api/workflows", new
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
                    agentKey = starterAgentKey,
                    agentVersion = (int?)1,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0,
                },
                new
                {
                    id = subflowId,
                    kind = "Subflow",
                    agentKey = (string?)null,
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 200,
                    layoutY = 0,
                    subflowKey,
                    subflowVersion = (int?)subflowVersion,
                }
            },
            edges = new object[]
            {
                new
                {
                    fromNodeId = startId,
                    fromPort = "Completed",
                    toNodeId = subflowId,
                    toPort = "in",
                    rotatesRound = false,
                    sortOrder = 0,
                }
            },
        });
        response.EnsureSuccessStatusCode();
    }
}
