using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.Tests.Integration;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Integration tests for HAA-10's `save_workflow_package` tool. The tool itself does not mutate;
/// it runs <see cref="IWorkflowPackageImporter.PreviewAsync"/> for self-containment validation
/// and returns the verdict for the chat UI to render as a confirmation chip. We verify the three
/// branches: malformed args (error), unresolvable package (status=invalid + missingReferences),
/// and a valid round-trippable package (status=preview_ok with item counts).
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class SaveWorkflowPackageToolTests : IClassFixture<CodeFlowApiFactory>, IAsyncLifetime
{
    private readonly CodeFlowApiFactory factory;

    public SaveWorkflowPackageToolTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    public async Task InitializeAsync()
    {
        AgentConfigRepository.ClearCacheForTests();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        db.WorkflowEdges.RemoveRange(db.WorkflowEdges);
        db.WorkflowNodes.RemoveRange(db.WorkflowNodes);
        db.WorkflowInputs.RemoveRange(db.WorkflowInputs);
        db.Workflows.RemoveRange(db.Workflows);
        db.Agents.RemoveRange(db.Agents);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Invoke_WithoutPackageArgument_ReturnsError()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        var result = await tool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("package");
    }

    [Fact]
    public async Task Invoke_WithRefThatExistsNeitherInPackageNorDb_ReturnsPreviewConflicts()
    {
        // A workflow node references an agent that's neither embedded nor in the target library.
        // The validator no longer treats this as a structural rejection — the planner emits a
        // Conflict plan item so the LLM can see which specific ref failed to resolve.
        var package = BuildPackage(
            entryKey: "lonely-flow",
            entryVersion: 1,
            agentKeyForFirstNode: "missing-agent",
            agentVersion: 7,
            includeAgentInPackage: false);
        var args = JsonSerializer.SerializeToElement(new { package = JsonSerializer.SerializeToElement(package) });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse(
            because: "a conflict-laden preview is a reportable verdict for the LLM, not a tool failure");

        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("preview_conflicts");
        parsed.GetProperty("canApply").GetBoolean().Should().BeFalse();
        parsed.GetProperty("conflictCount").GetInt32().Should().BeGreaterThan(0);

        var conflictMessages = parsed.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("action").GetString() == "Conflict")
            .Select(item => item.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();
        conflictMessages.Should().Contain(message => message != null && message.Contains("missing-agent"),
            because: "the LLM must see which agent failed to resolve so it can fix the package");
    }

    [Fact]
    public async Task Invoke_WithRefOmittedButExistsInDb_ReturnsPreviewOkWithReuse()
    {
        // The assistant's common case after a get_workflow_package call: it embeds the new
        // workflow but doesn't re-embed the existing agent (since the truncated tool result
        // can't reproduce the agent's full system prompt verbatim). The importer must resolve
        // the unembedded ref against the DB and treat it as Reuse.
        const string agentKey = "haa10-resolver-fallback-writer";
        await SeedAgentAsync(agentKey);

        var package = BuildPackage(
            entryKey: "lonely-flow",
            entryVersion: 1,
            agentKeyForFirstNode: agentKey,
            agentVersion: 1,
            includeAgentInPackage: false);
        var args = JsonSerializer.SerializeToElement(new { package = JsonSerializer.SerializeToElement(package) });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("preview_ok");
        parsed.GetProperty("canApply").GetBoolean().Should().BeTrue();
        parsed.GetProperty("reuseCount").GetInt32().Should().BeGreaterThan(0,
            because: "the existing agent should have been resolved from the DB and reported as Reuse");

        var reuseItems = parsed.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("action").GetString() == "Reuse"
                && item.GetProperty("kind").GetString() == "Agent")
            .ToArray();
        reuseItems.Should().Contain(item => item.GetProperty("key").GetString() == agentKey,
            because: "the unembedded agent ref should appear as a DB-resolved Reuse item");
    }

    [Fact]
    public async Task Invoke_DraftPath_SnapshotsValidatedBytes_AndIsImmuneToLaterMutation()
    {
        // The big Codex finding: the chip must apply EXACTLY the bytes the user saw at preview
        // time, even if the live draft.cf-workflow-package.json gets patched/overwritten before
        // the user clicks Save. Drive the workspace-aware save tool with a draft of package A,
        // then mutate the on-disk live draft to a totally different package B. The save tool's
        // result must (a) carry a snapshot id and (b) the snapshot file on disk must contain
        // package A — so a subsequent apply-from-draft against that snapshot id imports A,
        // never B.
        const string agentKey = "haa10-snapshot-binding-writer";
        await SeedAgentAsync(agentKey);

        // Fresh isolated workspace dir for this test (don't pollute the fixture's shared root).
        var workspaceDir = Path.Combine(
            Path.GetTempPath(),
            "cf-snapshot-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);
        var workspace = new ToolWorkspaceContext(Guid.NewGuid(), workspaceDir);

        try
        {
            // Stage package A as the live draft.
            var packageA = await BuildSelfContainedPackageAsync("haa10-snapshot-binding-flow", agentKey);
            await WorkflowPackageDraftStore.WriteAsync(
                workspace,
                JsonNode.Parse(JsonSerializer.Serialize(packageA, JsonSerializerOptions.Web))!,
                CancellationToken.None);

            // Build a workspace-aware SaveWorkflowPackageTool via the factory and invoke with
            // no args (the draft-path code we're verifying).
            await using var scope = factory.Services.CreateAsyncScope();
            var draftFactory = scope.ServiceProvider.GetRequiredService<WorkflowDraftAssistantToolFactory>();
            var saveTool = draftFactory.Build(workspace)
                .OfType<SaveWorkflowPackageTool>()
                .Single();

            var result = await saveTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

            result.IsError.Should().BeFalse();
            var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
            parsed.GetProperty("status").GetString().Should().Be("preview_ok");
            parsed.GetProperty("packageSource").GetString().Should().Be("draft");
            var snapshotIdString = parsed.GetProperty("snapshotId").GetString();
            snapshotIdString.Should().NotBeNullOrEmpty(
                because: "the draft path must mint a snapshot id so the chip binds to immutable bytes");
            var snapshotId = Guid.Parse(snapshotIdString!);

            // Snapshot file is on disk and contains package A.
            var snapshotPath = WorkflowPackageDraftStore.ResolveSnapshotPath(workspace, snapshotId);
            File.Exists(snapshotPath).Should().BeTrue();
            var snapshotBytes = await File.ReadAllTextAsync(snapshotPath);
            snapshotBytes.Should().Contain("haa10-snapshot-binding-flow",
                because: "the snapshot must hold package A's bytes");

            // Now overwrite the LIVE draft with a different package B (simulating the LLM
            // patching the draft after the chip rendered). The snapshot must NOT change.
            var packageB = await BuildSelfContainedPackageAsync("haa10-different-flow", agentKey);
            await WorkflowPackageDraftStore.WriteAsync(
                workspace,
                JsonNode.Parse(JsonSerializer.Serialize(packageB, JsonSerializerOptions.Web))!,
                CancellationToken.None);

            var snapshotBytesAfterMutation = await File.ReadAllTextAsync(snapshotPath);
            snapshotBytesAfterMutation.Should().Be(snapshotBytes,
                because: "live-draft mutation must not touch the immutable snapshot");
            snapshotBytesAfterMutation.Should().NotContain("haa10-different-flow");
        }
        finally
        {
            if (Directory.Exists(workspaceDir))
            {
                Directory.Delete(workspaceDir, recursive: true);
            }
        }
    }

    private async Task<WorkflowPackage> BuildSelfContainedPackageAsync(string workflowKey, string agentKey)
    {
        await SeedWorkflowAsync(workflowKey, agentKey: agentKey, agentVersion: 1);
        await using var scope = factory.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkflowPackageResolver>();
        return await resolver.ResolveAsync(workflowKey, 1);
    }

    [Fact]
    public async Task Invoke_DraftPath_EmitsSnapshotIdInFormatTheApplyEndpointCanBind()
    {
        // Regression for the silent-400 chip failure: the tool emitted snapshotId as
        // `Guid.ToString("N")` (32 hex, no dashes) for "compact transport," but the
        // apply-from-draft endpoint binds the body to a `Guid` field via System.Text.Json,
        // which only accepts the 36-char "D" (hyphenated) format. The result was a 400
        // with an empty body (binding failure short-circuits the handler's catch-all) and
        // no path forward for the user. Pin the contract here so a future "compact" tweak
        // can't silently re-break the chip.
        const string agentKey = "haa10-snapshot-format-writer";
        await SeedAgentAsync(agentKey);

        var workspaceDir = Path.Combine(
            Path.GetTempPath(),
            "cf-snapshot-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);
        var workspace = new ToolWorkspaceContext(Guid.NewGuid(), workspaceDir);

        try
        {
            var package = await BuildSelfContainedPackageAsync("haa10-snapshot-format-flow", agentKey);
            await WorkflowPackageDraftStore.WriteAsync(
                workspace,
                JsonNode.Parse(JsonSerializer.Serialize(package, JsonSerializerOptions.Web))!,
                CancellationToken.None);

            await using var scope = factory.Services.CreateAsyncScope();
            var draftFactory = scope.ServiceProvider.GetRequiredService<WorkflowDraftAssistantToolFactory>();
            var saveTool = draftFactory.Build(workspace).OfType<SaveWorkflowPackageTool>().Single();

            var result = await saveTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
            var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
            var snapshotIdString = parsed.GetProperty("snapshotId").GetString();
            snapshotIdString.Should().NotBeNullOrEmpty();

            // The chat panel sends the chip body as `{ conversationId, snapshotId }` where
            // both values arrive at the API as strings. Mimic the apply-from-draft DTO bind
            // exactly: System.Text.Json with the same Web defaults the framework configures
            // for minimal-API request bodies. If this throws, the chip fires and the user
            // sees an empty-body 400.
            var bodyJson = $$"""{"conversationId":"{{Guid.NewGuid()}}","snapshotId":"{{snapshotIdString}}"}""";
            var act = () => JsonSerializer.Deserialize<ApplyFromDraftBindingShape>(
                bodyJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            act.Should().NotThrow(
                because: "the chip POSTs this body to /api/workflows/package/apply-from-draft "
                    + "where it binds to a Guid; an unparseable id produces a 400 with empty body");
        }
        finally
        {
            if (Directory.Exists(workspaceDir))
            {
                Directory.Delete(workspaceDir, recursive: true);
            }
        }
    }

    private sealed record ApplyFromDraftBindingShape(Guid ConversationId, Guid SnapshotId);

    [Fact]
    public async Task Invoke_WithPackagePresentButNotObject_ReturnsHardError_NotDraftFallback()
    {
        // Codex finding (medium): when the LLM sends `{ "package": null }` or any non-object
        // value while a workspace exists, the original code silently fell through to the draft
        // path and applied whatever was on disk. The user thinks they're saving the value they
        // sent; they actually validate a stale draft. Must surface as a hard tool error so the
        // assistant fixes its argument shape rather than ship the wrong package.
        const string agentKey = "haa10-malformed-arg-writer";
        await SeedAgentAsync(agentKey);

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        // package: null
        var nullArgs = JsonDocument.Parse("""{ "package": null }""").RootElement;
        var nullResult = await tool.InvokeAsync(nullArgs, CancellationToken.None);
        nullResult.IsError.Should().BeTrue(
            because: "a null `package` value must be rejected, not silently dropped to the draft path");
        nullResult.ResultJson.Should().Contain("package");

        // package: "garbage"
        var stringArgs = JsonDocument.Parse("""{ "package": "garbage" }""").RootElement;
        var stringResult = await tool.InvokeAsync(stringArgs, CancellationToken.None);
        stringResult.IsError.Should().BeTrue(
            because: "a non-object `package` value must be rejected, not silently dropped to the draft path");

        // package: []
        var arrayArgs = JsonDocument.Parse("""{ "package": [] }""").RootElement;
        var arrayResult = await tool.InvokeAsync(arrayArgs, CancellationToken.None);
        arrayResult.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_WithIslandNodePackage_ReturnsInvalid_NotPreviewOk()
    {
        // The original user-reported failure mode: the assistant emits a package whose nodes are
        // present but uneconnected — a Start node and an Agent node with no edge between them.
        // PreviewAsync alone treated this as preview_ok; only the apply endpoint rejected it.
        // The tool now runs the same validators the apply endpoint runs and surfaces them as
        // status:invalid up front so the user is never told "click Save" on a doomed package.
        const string agentKey = "haa10-island-writer";
        await SeedAgentAsync(agentKey);

        var startId = Guid.NewGuid();
        var islandId = Guid.NewGuid();
        var packageJson = $$"""
        {
            "schemaVersion": "{{WorkflowPackageDefaults.SchemaVersion}}",
            "metadata": { "exportedFrom": "llm-test", "exportedAtUtc": "2026-04-30T00:00:00Z" },
            "entryPoint": { "key": "haa10-island-flow", "version": 1 },
            "workflows": [
                {
                    "key": "haa10-island-flow",
                    "version": 1,
                    "name": "Island flow",
                    "maxRoundsPerRound": 3,
                    "category": "Workflow",
                    "createdAtUtc": "2026-04-30T00:00:00Z",
                    "nodes": [
                        { "id": "{{startId}}", "kind": "Start", "agentKey": "{{agentKey}}", "agentVersion": 1, "outputPorts": ["Completed"], "layoutX": 0, "layoutY": 0 },
                        { "id": "{{islandId}}", "kind": "Agent", "agentKey": "{{agentKey}}", "agentVersion": 1, "outputPorts": ["Completed"], "layoutX": 200, "layoutY": 0 }
                    ],
                    "edges": [],
                    "inputs": []
                }
            ]
        }
        """;
        var packageElement = JsonDocument.Parse(packageJson).RootElement;
        var args = JsonSerializer.SerializeToElement(new { package = packageElement });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse(
            because: "an invalid package is a reportable verdict for the LLM, not a tool failure");
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("invalid",
            because: "the validator must run inline so the assistant doesn't tell the user 'click Save' on a doomed package");
        var errors = parsed.GetProperty("errors").EnumerateArray().ToArray();
        errors.Should().NotBeEmpty();
        errors[0].GetProperty("workflowKey").GetString().Should().Be("haa10-island-flow");
        errors[0].GetProperty("message").GetString().Should().Contain("not reachable from the Start node");
    }

    [Fact]
    public async Task Invoke_WithStringEnumNames_AcceptsLlmCanonicalShape()
    {
        // Regression: the assistant tool dispatcher previously used a serializer without
        // JsonStringEnumConverter while the HTTP /package/apply endpoint and every tool result
        // (get_workflow / get_agent) emitted enum names as strings. The LLM saw "Workflow",
        // "Agent", "Text" everywhere and reasonably mirrored that vocabulary, only to have
        // save_workflow_package reject it. We feed in a package serialized with explicit string
        // enum names to lock in that the dispatcher accepts what every other tool produces.
        const string agentKey = "haa10-string-enum-writer";
        await SeedAgentAsync(agentKey);
        await SeedWorkflowAsync("haa10-string-enum-flow", agentKey: agentKey, agentVersion: 1);

        WorkflowPackage package;
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var resolver = seedScope.ServiceProvider.GetRequiredService<IWorkflowPackageResolver>();
            package = await resolver.ResolveAsync("haa10-string-enum-flow", 1);
        }

        // Mimic what the LLM produces: camelCase property names (the assistant tool dispatcher
        // and HTTP API both expose camelCase JSON) plus string enum names.
        var llmStyleOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var packageElement = JsonSerializer.SerializeToElement(package, llmStyleOptions);

        // Sanity: the serialized package really does carry string enum names — otherwise the test
        // would be tautological (numeric integers always deserialized fine).
        var firstNodeKind = packageElement
            .GetProperty("workflows")[0]
            .GetProperty("nodes")[0]
            .GetProperty("kind");
        firstNodeKind.ValueKind.Should().Be(JsonValueKind.String,
            because: "the test must feed the dispatcher the LLM-canonical string form");

        var args = JsonSerializer.SerializeToElement(new { package = packageElement });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse(
            because: "string enum names are the LLM-canonical form and must round-trip");
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("preview_ok");
        parsed.GetProperty("canApply").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_WithMinimalLlmPackageOmittingOptionalCollections_DoesNotNullReference()
    {
        // Regression: the LLM frequently emits packages that include only the fields it has data
        // for — a single workflow plus a single agent — and silently omits agents[],
        // agentRoleAssignments[], roles[], skills[], mcpServers[], plus per-workflow tags[]. STJ
        // happily constructs the WorkflowPackage record with null for those non-nullable
        // IReadOnlyList<> properties, and both the validator and the importer planner NRE on the
        // first .Any()/.GroupBy() over a null list. Symptom in chat:
        //   {"error":"Tool 'save_workflow_package' threw NullReferenceException: ..."}
        // The importer must coerce nulls to empty collections so a partial package gets a clean
        // structural verdict instead of a stack trace.
        const string agentKey = "haa10-minimal-llm-writer";
        await SeedAgentAsync(agentKey);

        // Hand-craft the JSON the way the LLM does: only the keys it has values for.
        var workflowKey = "haa10-minimal-llm-flow";
        var startId = Guid.NewGuid();
        var agentNodeId = Guid.NewGuid();
        var minimalPackageJson = $$"""
        {
            "schemaVersion": "{{WorkflowPackageDefaults.SchemaVersion}}",
            "metadata": { "exportedFrom": "llm-test", "exportedAtUtc": "2026-04-30T00:00:00Z" },
            "entryPoint": { "key": "{{workflowKey}}", "version": 1 },
            "workflows": [
                {
                    "key": "{{workflowKey}}",
                    "version": 1,
                    "name": "Minimal LLM emission",
                    "maxRoundsPerRound": 1,
                    "category": "Workflow",
                    "createdAtUtc": "2026-04-30T00:00:00Z",
                    "nodes": [
                        { "id": "{{startId}}", "kind": "Start", "agentKey": "{{agentKey}}", "agentVersion": 1, "outputPorts": ["Completed"], "layoutX": 0, "layoutY": 0 },
                        { "id": "{{agentNodeId}}", "kind": "Agent", "agentKey": "{{agentKey}}", "agentVersion": 1, "outputPorts": ["Completed"], "layoutX": 200, "layoutY": 0 }
                    ],
                    "edges": [
                        { "fromNodeId": "{{startId}}", "fromPort": "Completed", "toNodeId": "{{agentNodeId}}", "toPort": "in", "rotatesRound": false, "sortOrder": 0 }
                    ],
                    "inputs": []
                }
            ]
        }
        """;
        var packageElement = JsonDocument.Parse(minimalPackageJson).RootElement;
        var args = JsonSerializer.SerializeToElement(new { package = packageElement });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse(
            because: "a partial package missing optional collections must produce a structural verdict, not NullReferenceException");
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("preview_ok");
        parsed.GetProperty("canApply").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_WithSelfContainedPackage_ReturnsPreviewOk()
    {
        // Use the resolver against a real seeded workflow + agent so the package is self-
        // contained by construction. The tool then validates it via PreviewAsync.
        const string agentKey = "haa10-writer";
        await SeedAgentAsync(agentKey);
        await SeedWorkflowAsync("haa10-flow", agentKey: agentKey, agentVersion: 1);

        WorkflowPackage package;
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var resolver = seedScope.ServiceProvider.GetRequiredService<IWorkflowPackageResolver>();
            package = await resolver.ResolveAsync("haa10-flow", 1);
        }

        var args = JsonSerializer.SerializeToElement(new
        {
            package = JsonSerializer.SerializeToElement(package),
            note = "from-test",
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("preview_ok");
        parsed.GetProperty("canApply").GetBoolean().Should().BeTrue();
        parsed.GetProperty("entryPoint").GetProperty("key").GetString().Should().Be("haa10-flow");
        parsed.GetProperty("entryPoint").GetProperty("version").GetInt32().Should().Be(1);
        parsed.GetProperty("note").GetString().Should().Be("from-test");
        // The seeded workflow is already in the library, so the importer should report Reuse not
        // Create. (We're "saving" a package that already matches what's stored — the assistant's
        // common case during refinement.)
        parsed.GetProperty("reuseCount").GetInt32().Should().BeGreaterThan(0);
        parsed.GetProperty("message").GetString().Should().Contain("Save");
    }

    private async Task SeedAgentAsync(string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var configJson = JsonSerializer.Serialize(new
        {
            type = "agent",
            provider = "anthropic",
            model = "claude-sonnet-4-6",
            systemPrompt = "You write things.",
            outputs = new[] { new { kind = "Completed" } },
        });

        db.Agents.Add(new AgentConfigEntity
        {
            Key = key,
            Version = 1,
            ConfigJson = configJson,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "haa10-test",
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedWorkflowAsync(string key, string agentKey, int agentVersion)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();

        var startNodeId = Guid.NewGuid();
        var agentNodeId = Guid.NewGuid();

        var draft = new WorkflowDraft(
            Key: key,
            Name: "HAA-10 test flow",
            MaxRoundsPerRound: 3,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: agentKey,
                    AgentVersion: agentVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Completed" },
                    LayoutX: 0,
                    LayoutY: 0),
                new WorkflowNodeDraft(
                    Id: agentNodeId,
                    Kind: WorkflowNodeKind.Agent,
                    AgentKey: agentKey,
                    AgentVersion: agentVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Completed" },
                    LayoutX: 200,
                    LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdgeDraft(startNodeId, "Completed", agentNodeId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInputDraft>());

        await repo.CreateNewVersionAsync(draft);
    }

    /// <summary>
    /// Build a minimal package by hand — used for the unresolvable case where we want to feed in
    /// a workflow that references an agent we deliberately omit from the package's agents[]
    /// array. The resolver path can't help here because the seeded data wouldn't reflect the
    /// missing-reference scenario.
    /// </summary>
    private static WorkflowPackage BuildPackage(
        string entryKey,
        int entryVersion,
        string agentKeyForFirstNode,
        int agentVersion,
        bool includeAgentInPackage)
    {
        var startId = Guid.NewGuid();
        var agentNodeId = Guid.NewGuid();

        var workflow = new WorkflowPackageWorkflow(
            Key: entryKey,
            Version: entryVersion,
            Name: "Lonely",
            MaxRoundsPerRound: 3,
            Category: WorkflowCategory.Workflow,
            Tags: Array.Empty<string>(),
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowPackageWorkflowNode(
                    Id: startId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: agentKeyForFirstNode,
                    AgentVersion: agentVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Completed" },
                    LayoutX: 0,
                    LayoutY: 0),
                new WorkflowPackageWorkflowNode(
                    Id: agentNodeId,
                    Kind: WorkflowNodeKind.Agent,
                    AgentKey: agentKeyForFirstNode,
                    AgentVersion: agentVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Completed" },
                    LayoutX: 200,
                    LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowPackageWorkflowEdge(
                    FromNodeId: startId,
                    FromPort: "Completed",
                    ToNodeId: agentNodeId,
                    ToPort: WorkflowEdge.DefaultInputPort,
                    RotatesRound: false,
                    SortOrder: 0),
            },
            Inputs: Array.Empty<WorkflowPackageWorkflowInput>());

        var agents = includeAgentInPackage
            ? new[]
            {
                new WorkflowPackageAgent(
                    Key: agentKeyForFirstNode,
                    Version: agentVersion,
                    Kind: AgentKind.Agent,
                    Config: JsonNode.Parse("""{"provider":"anthropic","model":"claude-sonnet-4-6","systemPrompt":"You write things.","outputs":[{"kind":"Completed"}]}"""),
                    CreatedAtUtc: DateTime.UtcNow,
                    CreatedBy: "haa10-test",
                    Outputs: new[] { new WorkflowPackageAgentOutput("Completed", null, null) }),
            }
            : Array.Empty<WorkflowPackageAgent>();

        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata("haa10-test", DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(entryKey, entryVersion),
            Workflows: new[] { workflow },
            Agents: agents,
            AgentRoleAssignments: Array.Empty<WorkflowPackageAgentRoleAssignment>(),
            Roles: Array.Empty<WorkflowPackageRole>(),
            Skills: Array.Empty<WorkflowPackageSkill>(),
            McpServers: Array.Empty<WorkflowPackageMcpServer>());
    }
}
