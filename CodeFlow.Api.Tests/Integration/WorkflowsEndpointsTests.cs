using CodeFlow.Persistence;
using CodeFlow.Runtime.Mcp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
                    outputScript = (string?)null,
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
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed", "Approved", "Rejected" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = reviewerId,
                    kind = "Agent",
                    agentKey = "wf-reviewer",
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed", "Approved", "Rejected" },
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

    [Fact]
    public async Task ExportPackage_ReturnsJsonDownloadForSelectedWorkflowVersion()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-export-writer");

        var startId = Guid.NewGuid();
        var create = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "export-flow",
            name = "Export flow",
            maxRoundsPerRound = 3,
            category = "Subflow",
            tags = new[] { "portable" },
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "wf-export-writer",
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await client.GetAsync("/api/workflows/export-flow/1/package");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition?.FileName.Should().Be("export-flow-v1-package.json");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("schemaVersion").GetString().Should().Be("codeflow.workflow-package.v1");
        root.GetProperty("entryPoint").GetProperty("key").GetString().Should().Be("export-flow");
        root.GetProperty("entryPoint").GetProperty("version").GetInt32().Should().Be(1);

        var workflow = root.GetProperty("workflows").EnumerateArray().Single();
        workflow.GetProperty("category").GetString().Should().Be("Subflow");
        workflow.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).Should().Equal("portable");
        workflow.GetProperty("nodes").EnumerateArray().Single()
            .GetProperty("agentVersion").GetInt32().Should().Be(1);

        root.GetProperty("agents").EnumerateArray().Single()
            .GetProperty("key").GetString().Should().Be("wf-export-writer");
    }

    [Fact]
    public async Task PreviewPackageImport_ReusesMatchingExportedPackage()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-preview-writer");

        var startId = Guid.NewGuid();
        var create = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "preview-flow",
            name = "Preview flow",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "wf-preview-writer",
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var packageJson = await client.GetStringAsync("/api/workflows/preview-flow/1/package");
        var response = await client.PostAsync(
            "/api/workflows/package/preview",
            new StringContent(packageJson, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("canApply").GetBoolean().Should().BeTrue();
        root.GetProperty("conflictCount").GetInt32().Should().Be(0);
        root.GetProperty("reuseCount").GetInt32().Should().BeGreaterThanOrEqualTo(2);
        root.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("action").GetString())
            .Should().OnlyContain(action => action == "Reuse");
    }

    [Fact]
    public async Task ApplyPackageImport_CreatesWorkflowAndDependencies()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-apply-source-writer");

        var startId = Guid.NewGuid();
        var create = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "apply-source-flow",
            name = "Apply source flow",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "wf-apply-source-writer",
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var packageJson = await client.GetStringAsync("/api/workflows/apply-source-flow/1/package");
        using var packageDocument = JsonDocument.Parse(packageJson);
        var package = packageDocument.RootElement.Clone();
        var rewritten = RewritePackageKeys(
            package,
            fromWorkflowKey: "apply-source-flow",
            toWorkflowKey: "apply-target-flow",
            fromAgentKey: "wf-apply-source-writer",
            toAgentKey: "wf-apply-target-writer");

        var apply = await client.PostAsync(
            "/api/workflows/package/apply",
            new StringContent(rewritten, Encoding.UTF8, "application/json"));

        apply.StatusCode.Should().Be(HttpStatusCode.OK);
        using var resultDocument = JsonDocument.Parse(await apply.Content.ReadAsStringAsync());
        resultDocument.RootElement.GetProperty("conflictCount").GetInt32().Should().Be(0);
        resultDocument.RootElement.GetProperty("createCount").GetInt32().Should().BeGreaterThanOrEqualTo(2);

        var imported = await client.GetFromJsonAsync<WorkflowDetailPayload>("/api/workflows/apply-target-flow/1");
        imported.Should().NotBeNull();
        imported!.Key.Should().Be("apply-target-flow");
        imported.Nodes.Should().ContainSingle()
            .Which.AgentKey.Should().Be("wf-apply-target-writer");

        var agent = await client.GetAsync("/api/agents/wf-apply-target-writer/1");
        agent.StatusCode.Should().Be(HttpStatusCode.OK);

        var workflows = await client.GetFromJsonAsync<IReadOnlyList<WorkflowSummaryPayload>>("/api/workflows");
        workflows.Should().NotBeNull();
        workflows!.Should().Contain(workflow =>
            workflow.Key == "apply-target-flow" &&
            workflow.LatestVersion == 1);

        var agents = await client.GetFromJsonAsync<IReadOnlyList<AgentSummaryPayload>>("/api/agents");
        agents.Should().NotBeNull();
        agents!.Should().Contain(agentSummary =>
            agentSummary.Key == "wf-apply-target-writer" &&
            agentSummary.LatestVersion == 1);
    }

    [Fact]
    public async Task ApplyPackageImport_BumpsWorkflowVersion_WhenSameVersionDiffers()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-conflict-writer");

        var startId = Guid.NewGuid();
        var create = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "conflict-flow",
            name = "Conflict flow",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "wf-conflict-writer",
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var packageJson = await client.GetStringAsync("/api/workflows/conflict-flow/1/package");
        var conflictingPackage = packageJson.Replace("Conflict flow", "Different imported flow", StringComparison.Ordinal);

        var preview = await client.PostAsync(
            "/api/workflows/package/preview",
            new StringContent(conflictingPackage, Encoding.UTF8, "application/json"));

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        using var previewDocument = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        previewDocument.RootElement.GetProperty("canApply").GetBoolean().Should().BeTrue();
        previewDocument.RootElement.GetProperty("conflictCount").GetInt32().Should().Be(0);
        previewDocument.RootElement.GetProperty("entryPoint").GetProperty("version").GetInt32().Should().Be(2);
        previewDocument.RootElement.GetProperty("items").EnumerateArray()
            .Should().Contain(item =>
                item.GetProperty("kind").GetString() == "Workflow" &&
                item.GetProperty("key").GetString() == "conflict-flow" &&
                item.GetProperty("version").GetInt32() == 2 &&
                item.GetProperty("action").GetString() == "Create" &&
                item.GetProperty("message").GetString()!.Contains("imported v1", StringComparison.Ordinal));

        var apply = await client.PostAsync(
            "/api/workflows/package/apply",
            new StringContent(conflictingPackage, Encoding.UTF8, "application/json"));

        apply.StatusCode.Should().Be(HttpStatusCode.OK);

        var imported = await client.GetFromJsonAsync<WorkflowDetailPayload>("/api/workflows/conflict-flow/2");
        imported.Should().NotBeNull();
        imported!.Name.Should().Be("Different imported flow");
    }

    [Fact]
    public async Task ApplyPackageImport_BumpsWorkflowPins_WhenAgentVersionIsBumped()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-agent-remap-writer");

        var startId = Guid.NewGuid();
        var create = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "agent-remap-flow",
            name = "Agent remap flow",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "wf-agent-remap-writer",
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var packageJson = await client.GetStringAsync("/api/workflows/agent-remap-flow/1/package");
        var package = JsonNode.Parse(packageJson)!.AsObject();
        var agent = package["agents"]!.AsArray()[0]!.AsObject();
        agent["config"]!.AsObject()["systemPrompt"] = "Do imported work.";
        var changedPackage = package.ToJsonString();

        var preview = await client.PostAsync(
            "/api/workflows/package/preview",
            new StringContent(changedPackage, Encoding.UTF8, "application/json"));

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        using var previewDocument = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        previewDocument.RootElement.GetProperty("canApply").GetBoolean().Should().BeTrue();
        previewDocument.RootElement.GetProperty("conflictCount").GetInt32().Should().Be(0);
        previewDocument.RootElement.GetProperty("entryPoint").GetProperty("version").GetInt32().Should().Be(2);
        previewDocument.RootElement.GetProperty("items").EnumerateArray()
            .Should().Contain(item =>
                item.GetProperty("kind").GetString() == "Agent" &&
                item.GetProperty("key").GetString() == "wf-agent-remap-writer" &&
                item.GetProperty("version").GetInt32() == 2);
        previewDocument.RootElement.GetProperty("items").EnumerateArray()
            .Should().Contain(item =>
                item.GetProperty("kind").GetString() == "Workflow" &&
                item.GetProperty("key").GetString() == "agent-remap-flow" &&
                item.GetProperty("version").GetInt32() == 2);

        var apply = await client.PostAsync(
            "/api/workflows/package/apply",
            new StringContent(changedPackage, Encoding.UTF8, "application/json"));

        apply.StatusCode.Should().Be(HttpStatusCode.OK);

        var imported = await client.GetFromJsonAsync<WorkflowDetailPayload>("/api/workflows/agent-remap-flow/2");
        imported.Should().NotBeNull();
        imported!.Nodes.Should().ContainSingle()
            .Which.AgentVersion.Should().Be(2);

        var importedAgent = await client.GetAsync("/api/agents/wf-agent-remap-writer/2");
        importedAgent.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PreviewPackageImport_ReportsConflict_WhenTargetHasHigherWorkflowVersion()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-higher-conflict-writer");

        var startId = Guid.NewGuid();
        var create = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "higher-conflict-flow",
            name = "Higher conflict flow",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "wf-higher-conflict-writer",
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var packageJson = await client.GetStringAsync("/api/workflows/higher-conflict-flow/1/package");

        var update = await client.PutAsJsonAsync("/api/workflows/higher-conflict-flow", new
        {
            name = "Higher conflict flow v2",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "wf-higher-conflict-writer",
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var preview = await client.PostAsync(
            "/api/workflows/package/preview",
            new StringContent(packageJson, Encoding.UTF8, "application/json"));

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        using var previewDocument = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        previewDocument.RootElement.GetProperty("canApply").GetBoolean().Should().BeFalse();
        previewDocument.RootElement.GetProperty("conflictCount").GetInt32().Should().BeGreaterThan(0);
        previewDocument.RootElement.GetProperty("items").EnumerateArray()
            .Should().Contain(item =>
                item.GetProperty("kind").GetString() == "Workflow" &&
                item.GetProperty("key").GetString() == "higher-conflict-flow" &&
                item.GetProperty("version").GetInt32() == 1 &&
                item.GetProperty("action").GetString() == "Conflict" &&
                item.GetProperty("message").GetString()!.Contains("higher than imported v1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewPackageImport_RejectsIncompleteDependencyClosure()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-missing-closure-writer");

        var startId = Guid.NewGuid();
        var create = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "missing-closure-flow",
            name = "Missing closure flow",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "wf-missing-closure-writer",
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var packageJson = await client.GetStringAsync("/api/workflows/missing-closure-flow/1/package");
        var package = JsonNode.Parse(packageJson)!.AsObject();
        package["agents"] = new JsonArray();

        var preview = await client.PostAsync(
            "/api/workflows/package/preview",
            new StringContent(package.ToJsonString(), Encoding.UTF8, "application/json"));

        preview.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await preview.Content.ReadAsStringAsync();
        body.Should().Contain("references missing agent");
    }

    [Fact]
    public async Task ApplyPackageImport_TransformDemoLibraryPackage_RoundTripsBothModes()
    {
        using var client = factory.CreateClient();

        var packagePath = LocateLibraryPackage("transform-demo-v1-package.json");
        var packageJson = await File.ReadAllTextAsync(packagePath);

        var apply = await client.PostAsync(
            "/api/workflows/package/apply",
            new StringContent(packageJson, Encoding.UTF8, "application/json"));

        apply.StatusCode.Should().Be(HttpStatusCode.OK);
        using var resultDoc = JsonDocument.Parse(await apply.Content.ReadAsStringAsync());
        resultDoc.RootElement.GetProperty("conflictCount").GetInt32().Should().Be(0);
        resultDoc.RootElement.GetProperty("createCount").GetInt32().Should().BeGreaterThanOrEqualTo(3);

        var detailJson = await client.GetStringAsync("/api/workflows/transform-demo/1");
        using var detailDoc = JsonDocument.Parse(detailJson);
        var nodes = detailDoc.RootElement.GetProperty("nodes").EnumerateArray().ToList();
        nodes.Should().HaveCount(4);

        var transformNodes = nodes
            .Where(n => string.Equals(n.GetProperty("kind").GetString(), "Transform", StringComparison.Ordinal))
            .ToList();
        transformNodes.Should().HaveCount(2, "the demo features one Transform per output mode");

        var jsonModeNode = transformNodes.Single(n =>
            string.Equals(n.GetProperty("outputType").GetString(), "json", StringComparison.Ordinal));
        jsonModeNode.GetProperty("template").GetString().Should().Contain("string.literal",
            "the JSON-mode template uses Scriban's string.literal helper to safely escape arbitrary string content");

        var stringModeNode = transformNodes.Single(n =>
            string.Equals(n.GetProperty("outputType").GetString(), "string", StringComparison.Ordinal));
        stringModeNode.GetProperty("template").GetString().Should().Contain("# {{ input.title }}",
            "the string-mode template renders a Markdown summary headed by the topic title");

        // Render the JSON-mode template against a representative analyzer payload via the TN-6
        // preview endpoint and confirm it actually emits valid JSON. Without this, the package
        // could import cleanly but fail at saga runtime if the template references an unknown
        // Scriban helper.
        var renderRequest = new
        {
            template = jsonModeNode.GetProperty("template").GetString(),
            outputType = "json",
            input = JsonNode.Parse("""
                {"title":"Sample \"topic\"","summary":"S","bullets":["alpha","\"quoted\"","gamma"],"risks":["r1"]}
                """),
            context = new Dictionary<string, object>(),
            workflow = new Dictionary<string, object> { ["topic"] = "Demo" }
        };
        var render = await client.PostAsJsonAsync(
            "/api/workflows/templates/render-transform-preview",
            renderRequest);
        render.StatusCode.Should().Be(HttpStatusCode.OK);
        using var renderDoc = JsonDocument.Parse(await render.Content.ReadAsStringAsync());
        renderDoc.RootElement.TryGetProperty("jsonParseError", out var parseError).Should().BeFalse(
            $"the JSON-mode template must produce valid JSON; got jsonParseError='{(parseError.ValueKind == JsonValueKind.String ? parseError.GetString() : "(none)")}' and rendered='{renderDoc.RootElement.GetProperty("rendered").GetString()}'");
        var parsed = renderDoc.RootElement.GetProperty("parsed");
        parsed.GetProperty("title").GetString().Should().Be("Sample \"topic\"");
        parsed.GetProperty("bullets").EnumerateArray().Select(e => e.GetString()).Should()
            .BeEquivalentTo(new[] { "alpha", "\"quoted\"", "gamma" });
        parsed.GetProperty("risks").EnumerateArray().Select(e => e.GetString()).Should()
            .BeEquivalentTo(new[] { "r1" });
    }

    [Fact]
    public async Task ApplyPackageImport_SwarmBenchBaselineV1_RoundTripsSingleAgentWorkflow()
    {
        // sc-82 — Variant V1 of the swarm-bench harness (Epic 38). Apples-to-apples comparison
        // floor for V2 (Sequential subflow) and V3 (Swarm node). End-to-end import sanity:
        // deserialize, resolve references, write through the importer, then query the workflow
        // back. Two nodes, two agents, one terminal (unwired) port — the simplest variant.
        using var client = factory.CreateClient();

        var packagePath = LocateLibraryPackage("swarm-bench-baseline-v1-package.json");
        var packageJson = await File.ReadAllTextAsync(packagePath);

        var apply = await client.PostAsync(
            "/api/workflows/package/apply",
            new StringContent(packageJson, Encoding.UTF8, "application/json"));

        apply.StatusCode.Should().Be(HttpStatusCode.OK);
        using var resultDoc = JsonDocument.Parse(await apply.Content.ReadAsStringAsync());
        resultDoc.RootElement.GetProperty("conflictCount").GetInt32().Should().Be(0);
        resultDoc.RootElement.GetProperty("createCount").GetInt32().Should().BeGreaterThanOrEqualTo(3,
            "package contains 1 workflow + 2 agents");

        var detailJson = await client.GetStringAsync("/api/workflows/swarm-bench-baseline/1");
        using var detailDoc = JsonDocument.Parse(detailJson);
        var nodes = detailDoc.RootElement.GetProperty("nodes").EnumerateArray().ToList();
        nodes.Should().HaveCount(2);

        var startNode = nodes.Single(n =>
            string.Equals(n.GetProperty("kind").GetString(), "Start", StringComparison.Ordinal));
        startNode.GetProperty("inputScript").GetString().Should().Contain("setWorkflow('mission'",
            "the Start node seeds workflow.mission from the input artifact");
        startNode.GetProperty("agentKey").GetString().Should().Be("swarm-bench-baseline-init");

        var agentNode = nodes.Single(n =>
            string.Equals(n.GetProperty("kind").GetString(), "Agent", StringComparison.Ordinal));
        agentNode.GetProperty("agentKey").GetString().Should().Be("swarm-bench-baseline-agent");
        agentNode.GetProperty("outputPorts").EnumerateArray().Select(e => e.GetString()).Should()
            .BeEquivalentTo(new[] { "Answered" },
                "the answering agent has a single terminal port — unwired so the agent's message body is the workflow's terminal artifact");

        // One edge from Start.Continue → Agent.in; the answering agent's Answered port is unwired
        // and therefore terminal.
        var edges = detailDoc.RootElement.GetProperty("edges").EnumerateArray().ToList();
        edges.Should().ContainSingle();
        edges[0].GetProperty("rotatesRound").GetBoolean().Should().BeFalse();
    }

    private static string LocateLibraryPackage(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "workflows", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            $"Could not locate workflows/{fileName} by walking up from {AppContext.BaseDirectory}.");
    }

    [Fact]
    public async Task ExportPackage_RedactsMcpBearerToken_AndPreviewWarns()
    {
        using var client = factory.CreateClient();
        const string agentKey = "wf-secret-tool-writer";
        await SeedAgentAsync(client, agentKey);
        await SeedMcpRoleAsync(agentKey);

        var startId = Guid.NewGuid();
        var create = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "secret-tool-flow",
            name = "Secret tool flow",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey,
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var packageJson = await client.GetStringAsync("/api/workflows/secret-tool-flow/1/package");
        packageJson.Should().NotContain("super-secret-token");

        using var packageDocument = JsonDocument.Parse(packageJson);
        var server = packageDocument.RootElement.GetProperty("mcpServers").EnumerateArray().Single();
        server.GetProperty("key").GetString().Should().Be("secret-search");
        server.GetProperty("hasBearerToken").GetBoolean().Should().BeTrue();
        server.TryGetProperty("bearerToken", out _).Should().BeFalse();

        var preview = await client.PostAsync(
            "/api/workflows/package/preview",
            new StringContent(packageJson, Encoding.UTF8, "application/json"));

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        using var previewDocument = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        previewDocument.RootElement.GetProperty("warnings").EnumerateArray()
            .Select(warning => warning.GetString())
            .Should().Contain(warning => warning!.Contains("bearer token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PostValidate_ReturnsFindingsWithoutPersisting()
    {
        // F1 acceptance: POST /api/workflows/validate runs the pluggable rule pipeline against an
        // arbitrary workflow draft and returns structured findings — no DB write happens.
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-validate-writer");

        var startId = Guid.NewGuid();
        var validateResponse = await client.PostAsJsonAsync("/api/workflows/validate", new
        {
            key = "validate-flow",
            name = "Validate-only flow",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = startId,
                    kind = "Start",
                    agentKey = "wf-validate-writer",
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    inputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });

        validateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await validateResponse.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // Canary rule emits a Warning when Start node has no input script — confirms wiring.
        root.GetProperty("hasErrors").GetBoolean().Should().BeFalse();
        root.GetProperty("hasWarnings").GetBoolean().Should().BeTrue();
        var findings = root.GetProperty("findings").EnumerateArray().ToList();
        findings.Should().Contain(f =>
            f.GetProperty("ruleId").GetString() == "start-node-input-script-advisory" &&
            f.GetProperty("severity").GetString() == "Warning");

        // Validate did not persist — no row exists for this key.
        var listResponse = await client.GetAsync("/api/workflows/validate-flow");
        listResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
                    new { kind = "Escalated" },
                    new { kind = "Accept" },
                    new { kind = "Reject" }
                }
            }
        });
        response.EnsureSuccessStatusCode();
    }

    private async Task SeedMcpRoleAsync(string agentKey)
    {
        using var scope = factory.Services.CreateScope();
        var mcpRepository = scope.ServiceProvider.GetRequiredService<IMcpServerRepository>();
        var roleRepository = scope.ServiceProvider.GetRequiredService<IAgentRoleRepository>();

        var serverId = await mcpRepository.CreateAsync(new McpServerCreate(
            Key: "secret-search",
            DisplayName: "Secret Search",
            Transport: McpTransportKind.StreamableHttp,
            EndpointUrl: "http://localhost:8765/mcp",
            BearerTokenPlaintext: "super-secret-token",
            CreatedBy: "test"));

        await mcpRepository.ReplaceToolsAsync(serverId, new[]
        {
            new McpServerToolWrite(
                ToolName: "query",
                Description: "Search",
                ParametersJson: """{"type":"object"}""",
                IsMutating: false)
        });

        var roleId = await roleRepository.CreateAsync(new AgentRoleCreate(
            Key: "secret-search-role",
            DisplayName: "Secret Search Role",
            Description: null,
            CreatedBy: "test"));

        await roleRepository.ReplaceGrantsAsync(roleId, new[]
        {
            new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, "mcp:secret-search:query")
        });

        await roleRepository.ReplaceAssignmentsAsync(agentKey, new[] { roleId });
    }

    private static string RewritePackageKeys(
        JsonElement package,
        string fromWorkflowKey,
        string toWorkflowKey,
        string fromAgentKey,
        string toAgentKey)
    {
        var json = package.GetRawText()
            .Replace(fromWorkflowKey, toWorkflowKey, StringComparison.Ordinal)
            .Replace(fromAgentKey, toAgentKey, StringComparison.Ordinal)
            .Replace("Apply source flow", "Apply target flow", StringComparison.Ordinal);

        return json;
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
        string? OutputScript,
        IReadOnlyList<string> OutputPorts,
        string? InputScript = null,
        int? AgentVersion = null,
        string? SubflowKey = null,
        int? SubflowVersion = null);

    private sealed record EdgePayload(Guid FromNodeId, string FromPort, Guid ToNodeId, string ToPort, bool RotatesRound);

    private sealed record WorkflowSummaryPayload(string Key, int LatestVersion, string Name);

    private sealed record AgentSummaryPayload(string Key, int LatestVersion, string Type);

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
                    outputScript = classifierScript,
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
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
                    layoutX = 200,
                    layoutY = 0
                },
                new
                {
                    id = rejectId,
                    kind = "Agent",
                    agentKey = "wf-reject-flow",
                    agentVersion = (int?)null,
                    outputScript = (string?)null,
                    outputPorts = new[] { "Completed" },
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
        reloadedStart.OutputScript.Should().Be(classifierScript);
        reloadedStart.OutputPorts.Should().Equal("Accept", "Reject");

        var reloadedAccept = detail.Nodes.Single(n => n.Id == acceptId);
        reloadedAccept.OutputScript.Should().BeNull();
        reloadedAccept.OutputPorts.Should().Equal("Completed");
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
    public async Task ValidateScript_InputDirection_AcceptsSetInput()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows/validate-script", new
        {
            script = "setInput('normalized: ' + (input.topic || 'none'));",
            direction = "input"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ValidateScriptResponseShape>();
        body.Should().NotBeNull();
        body!.Ok.Should().BeTrue();
        body.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateScript_InputDirection_RejectsSetOutput()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows/validate-script", new
        {
            script = "setOutput('wrong verb');",
            direction = "input"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ValidateScriptResponseShape>();
        body.Should().NotBeNull();
        body!.Ok.Should().BeFalse();
        body.Errors.Should().NotBeEmpty();
        body.Errors[0].Message.Should().Contain("agent-attached");
    }

    [Fact]
    public async Task ValidateScript_OutputDirection_AcceptsSetOutputAndSetNodePath()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows/validate-script", new
        {
            script = """
                setOutput('# Summary');
                setNodePath('Completed');
                """,
            direction = "output"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ValidateScriptResponseShape>();
        body.Should().NotBeNull();
        body!.Ok.Should().BeTrue();
        body.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateScript_OutputDirection_RejectsSetInput()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows/validate-script", new
        {
            script = "setInput('wrong verb');",
            direction = "output"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ValidateScriptResponseShape>();
        body.Should().NotBeNull();
        body!.Ok.Should().BeFalse();
        body.Errors.Should().NotBeEmpty();
        body.Errors[0].Message.Should().Contain("agent-attached");
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
                    outputPorts = new[] { "Completed", "Escalated" },
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
                    outputPorts = new[] { "Completed", "Escalated" },
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
                    outputPorts = new[] { "Completed", "Escalated" },
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
                    outputPorts = new[] { "Completed", "Escalated" },
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
                    outputPorts = new[] { "Completed", "Escalated" },
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
                    outputPorts = new[] { "Completed", "Escalated" },
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

    [Fact]
    public async Task Post_Rejects_WhenReviewLoopMissingMaxRounds()
    {
        // Slice 6: ReviewLoop node must declare ReviewMaxRounds.
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-rl-missing-max-rounds");

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "rl-missing-max-rounds",
            name = "Missing max rounds",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    kind = "Start",
                    agentKey = "wf-rl-missing-max-rounds",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = Guid.NewGuid(),
                    kind = "ReviewLoop",
                    agentKey = (string?)null,
                    outputPorts = new[] { "Approved" },
                    layoutX = 250,
                    layoutY = 0,
                    subflowKey = "any-child",
                    subflowVersion = (int?)null,
                    reviewMaxRounds = (int?)null
                }
            },
            edges = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task Post_Rejects_WhenReviewLoopMaxRoundsOutOfBounds(int maxRounds)
    {
        // Slice 6: ReviewMaxRounds must be in [1, 10].
        using var client = factory.CreateClient();
        var agentKey = $"wf-rl-oob-{maxRounds}";
        await SeedAgentAsync(client, agentKey);

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = $"rl-oob-{maxRounds}",
            name = $"Out of bounds {maxRounds}",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    kind = "Start",
                    agentKey,
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = Guid.NewGuid(),
                    kind = "ReviewLoop",
                    agentKey = (string?)null,
                    outputPorts = new[] { "Approved" },
                    layoutX = 250,
                    layoutY = 0,
                    subflowKey = "any-child",
                    subflowVersion = (int?)null,
                    reviewMaxRounds = maxRounds
                }
            },
            edges = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_Rejects_WhenReviewLoopReferencesItself()
    {
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-rl-selfref-start");

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "rl-self-ref",
            name = "Self ref",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    kind = "Start",
                    agentKey = "wf-rl-selfref-start",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = Guid.NewGuid(),
                    kind = "ReviewLoop",
                    agentKey = (string?)null,
                    outputPorts = new[] { "Approved" },
                    layoutX = 250,
                    layoutY = 0,
                    subflowKey = "rl-self-ref",
                    subflowVersion = (int?)null,
                    reviewMaxRounds = 3
                }
            },
            edges = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_Accepts_ValidReviewLoopNode_ReferencingExistingChild_WithNullVersion()
    {
        // Slice 6 happy path: a valid ReviewLoop with a child reference (null version = latest
        // at save) saves successfully and the stored version is resolved to the child's latest.
        using var client = factory.CreateClient();
        await SeedAgentAsync(client, "wf-rl-parent-start");
        await SeedAgentAsync(client, "wf-rl-child-start");

        var childStartId = Guid.NewGuid();
        var childResponse = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "rl-valid-child",
            name = "RL valid child",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = childStartId,
                    kind = "Start",
                    agentKey = "wf-rl-child-start",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                }
            },
            edges = Array.Empty<object>()
        });
        childResponse.EnsureSuccessStatusCode();

        var parentStartId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            key = "rl-valid-parent",
            name = "RL valid parent",
            maxRoundsPerRound = 3,
            nodes = new object[]
            {
                new
                {
                    id = parentStartId,
                    kind = "Start",
                    agentKey = "wf-rl-parent-start",
                    outputPorts = new[] { "Completed" },
                    layoutX = 0,
                    layoutY = 0
                },
                new
                {
                    id = reviewLoopNodeId,
                    kind = "ReviewLoop",
                    agentKey = (string?)null,
                    outputPorts = new[] { "Approved" },
                    layoutX = 250,
                    layoutY = 0,
                    subflowKey = "rl-valid-child",
                    subflowVersion = (int?)null,
                    reviewMaxRounds = 3
                }
            },
            edges = new object[]
            {
                new { fromNodeId = parentStartId, fromPort = "Completed", toNodeId = reviewLoopNodeId, toPort = "in", rotatesRound = false }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // Verify the null version got resolved to 1 (the child's only version).
        var detail = await client.GetFromJsonAsync<System.Text.Json.JsonDocument>("/api/workflows/rl-valid-parent/1");
        detail.Should().NotBeNull();
        var node = detail!.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("kind").GetString() == "ReviewLoop");
        node.GetProperty("subflowVersion").GetInt32().Should().Be(1);
        node.GetProperty("reviewMaxRounds").GetInt32().Should().Be(3);
    }

    private sealed record ValidateScriptResponseShape(bool Ok, IReadOnlyList<ValidateScriptErrorShape> Errors);
    private sealed record ValidateScriptErrorShape(int Line, int Column, string Message);

    // ---------- TN-6: Transform-node template preview ----------

    [Fact]
    public async Task TransformPreview_StringMode_RendersAgainstInputContextWorkflowVars()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = "hello, {{ input.name }} ({{ input.count }}) ctx={{ context.greeting }} wf={{ workflow.flag }}",
            outputType = "string",
            input = new { name = "world", count = 3 },
            context = new Dictionary<string, object> { ["greeting"] = "ctxv" },
            workflow = new Dictionary<string, object> { ["flag"] = "wfv" }
        };

        var response = await client.PostAsJsonAsync("/api/workflows/templates/render-transform-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<TransformPreviewResponseShape>();
        payload!.Rendered.Should().Be("hello, world (3) ctx=ctxv wf=wfv");
        payload.Parsed.Should().BeNull();
        payload.JsonParseError.Should().BeNull();
    }

    [Fact]
    public async Task TransformPreview_JsonMode_HappyPath_ReturnsRenderedAndParsed()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = "{ \"value\": {{ input.x }}, \"label\": \"{{ context.name }}\" }",
            outputType = "json",
            input = new { x = 42 },
            context = new Dictionary<string, object> { ["name"] = "alpha" }
        };

        var response = await client.PostAsJsonAsync("/api/workflows/templates/render-transform-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<TransformPreviewResponseShape>();
        payload!.Rendered.Should().Be("{ \"value\": 42, \"label\": \"alpha\" }");
        payload.JsonParseError.Should().BeNull();
        payload.Parsed.Should().NotBeNull();
        payload.Parsed!.Value.GetProperty("value").GetInt32().Should().Be(42);
        payload.Parsed.Value.GetProperty("label").GetString().Should().Be("alpha");
    }

    [Fact]
    public async Task TransformPreview_JsonMode_InvalidJson_ReturnsRawAndParseError()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = "not json {{ input.text }}",
            outputType = "json",
            input = new { text = "world" }
        };

        var response = await client.PostAsJsonAsync("/api/workflows/templates/render-transform-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<TransformPreviewResponseShape>();
        payload!.Rendered.Should().Be("not json world");
        payload.Parsed.Should().BeNull();
        payload.JsonParseError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TransformPreview_RenderError_Returns422()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = "{{ for i in 0..5000 }}x{{ end }}",
            outputType = "string"
        };

        var response = await client.PostAsJsonAsync("/api/workflows/templates/render-transform-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadFromJsonAsync<TransformPreviewErrorResponseShape>();
        payload!.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TransformPreview_EmptyTemplate_ReturnsValidationProblem()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = "",
            outputType = "string"
        };

        var response = await client.PostAsJsonAsync("/api/workflows/templates/render-transform-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TransformPreview_InvalidOutputType_ReturnsValidationProblem()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = "hi",
            outputType = "yaml"
        };

        var response = await client.PostAsJsonAsync("/api/workflows/templates/render-transform-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TransformPreview_OmittedOutputType_DefaultsToString()
    {
        using var client = factory.CreateClient();

        var body = new
        {
            template = "hello {{ input.name }}",
            input = new { name = "anon" }
        };

        var response = await client.PostAsJsonAsync("/api/workflows/templates/render-transform-preview", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<TransformPreviewResponseShape>();
        payload!.Rendered.Should().Be("hello anon");
        payload.Parsed.Should().BeNull();
        payload.JsonParseError.Should().BeNull();
    }

    private sealed record TransformPreviewResponseShape(string Rendered, JsonElement? Parsed, string? JsonParseError);
    private sealed record TransformPreviewErrorResponseShape(string Error);
}
