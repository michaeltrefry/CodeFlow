using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.Assistant.Artifacts;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Unit tests for the four agent-package draft tools + the SaveAgentPackageTool. These
/// exercise the on-disk store, JSON Patch application, redaction rejection, and the
/// not-found / refusal branches without spinning up the full API host.
/// </summary>
public sealed class AgentPackageDraftToolsTests : IDisposable
{
    private readonly string tempDir;
    private readonly ToolWorkspaceContext workspace;
    private readonly TestArtifactRecorder recorder;

    public AgentPackageDraftToolsTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "cf-agent-draft-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        workspace = new ToolWorkspaceContext(Guid.NewGuid(), tempDir);
        recorder = new TestArtifactRecorder();
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SetDraft_WritesPackageToDisk_AndReturnsAgentSummary()
    {
        var tool = new SetAgentPackageDraftTool(workspace, recorder);
        var args = JsonDocument.Parse("""
        {
          "package": {
            "schemaVersion": "codeflow.agent-package.v1",
            "entryPoint": { "key": "writer", "version": 3 },
            "agents": [{ "key": "writer", "version": 3 }],
            "agentRoleAssignments": [{ "agentKey": "writer", "roleKeys": ["reviewer"] }],
            "roles": [{ "key": "reviewer" }],
            "skills": [{ "name": "triage" }],
            "mcpServers": []
          }
        }
        """).RootElement;

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("saved");
        parsed.GetProperty("path").GetString().Should().Be(AgentPackageDraftStore.DraftFileName);

        var summary = parsed.GetProperty("summary");
        summary.GetProperty("entryPoint").GetProperty("key").GetString().Should().Be("writer");
        summary.GetProperty("agentCount").GetInt32().Should().Be(1);
        summary.GetProperty("roleCount").GetInt32().Should().Be(1);
        summary.GetProperty("skillCount").GetInt32().Should().Be(1);
        summary.GetProperty("mcpServerCount").GetInt32().Should().Be(0);

        File.Exists(Path.Combine(tempDir, AgentPackageDraftStore.DraftFileName)).Should().BeTrue();
    }

    [Fact]
    public async Task SetDraft_SecondCall_FlagsWasOverwriteTrue_AndCarriesSharperNudge()
    {
        var tool = new SetAgentPackageDraftTool(workspace, recorder);
        var args = JsonDocument.Parse("""
        { "package": { "schemaVersion": "codeflow.agent-package.v1", "agents": [{ "key": "writer", "version": 1 }] } }
        """).RootElement;

        await tool.InvokeAsync(args, CancellationToken.None);
        var second = await tool.InvokeAsync(args, CancellationToken.None);

        second.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(second.ResultJson).RootElement;
        parsed.GetProperty("wasOverwrite").GetBoolean().Should().BeTrue();
        var message = parsed.GetProperty("message").GetString()!;
        message.Should().Contain("REPLACED in full");
        message.Should().Contain("patch_agent_package_draft");
        message.Should().Contain("get_agent_package_draft");
    }

    [Fact]
    public async Task GetDraft_AfterSet_ReturnsStoredPackage()
    {
        var setTool = new SetAgentPackageDraftTool(workspace, recorder);
        await setTool.InvokeAsync(
            JsonDocument.Parse("""{ "package": { "agents": [{ "key": "writer", "version": 1 }] } }""").RootElement,
            CancellationToken.None);

        var getTool = new GetAgentPackageDraftTool(workspace);
        var result = await getTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("ok");
        parsed.GetProperty("package").GetProperty("agents")[0].GetProperty("key").GetString().Should().Be("writer");
    }

    [Fact]
    public async Task GetDraft_WhenAbsent_ReturnsNotFound()
    {
        var getTool = new GetAgentPackageDraftTool(workspace);
        var result = await getTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("not_found");
        parsed.GetProperty("message").GetString().Should().Contain("set_agent_package_draft");
    }

    [Fact]
    public async Task PatchDraft_AppliesReplaceOp_ToAgentSystemPrompt()
    {
        var setTool = new SetAgentPackageDraftTool(workspace, recorder);
        await setTool.InvokeAsync(
            JsonDocument.Parse("""
            {
              "package": {
                "agents": [{
                  "key": "writer",
                  "version": 1,
                  "config": { "systemPrompt": "old" }
                }]
              }
            }
            """).RootElement,
            CancellationToken.None);

        var patchTool = new PatchAgentPackageDraftTool(workspace, recorder);
        var patchArgs = JsonDocument.Parse("""
        { "operations": [ { "op": "replace", "path": "/agents/0/config/systemPrompt", "value": "new" } ] }
        """).RootElement;
        var result = await patchTool.InvokeAsync(patchArgs, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("patched");
        parsed.GetProperty("operationsApplied").GetInt32().Should().Be(1);

        var getTool = new GetAgentPackageDraftTool(workspace);
        var getResult = await getTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        var draft = JsonDocument.Parse(getResult.ResultJson).RootElement.GetProperty("package");
        draft.GetProperty("agents")[0].GetProperty("config").GetProperty("systemPrompt").GetString().Should().Be("new");
    }

    [Fact]
    public async Task PatchDraft_FailingOp_DoesNotMutateDisk()
    {
        var setTool = new SetAgentPackageDraftTool(workspace, recorder);
        await setTool.InvokeAsync(
            JsonDocument.Parse("""{ "package": { "agents": [{ "key": "writer" }] } }""").RootElement,
            CancellationToken.None);
        var draftPath = AgentPackageDraftStore.ResolveDraftPath(workspace);
        var beforeBytes = File.ReadAllBytes(draftPath);

        var patchTool = new PatchAgentPackageDraftTool(workspace, recorder);
        var patchArgs = JsonDocument.Parse("""
        { "operations": [ { "op": "test", "path": "/agents/0/key", "value": "DOES-NOT-MATCH" } ] }
        """).RootElement;
        var result = await patchTool.InvokeAsync(patchArgs, CancellationToken.None);

        result.IsError.Should().BeTrue();
        // The on-disk draft must be byte-identical to its pre-patch state — atomic semantics.
        File.ReadAllBytes(draftPath).Should().Equal(beforeBytes);
    }

    [Fact]
    public async Task PatchDraft_WhenNoDraftExists_ReturnsError()
    {
        var patchTool = new PatchAgentPackageDraftTool(workspace, recorder);
        var args = JsonDocument.Parse("""
        { "operations": [ { "op": "add", "path": "/agents/-", "value": {} } ] }
        """).RootElement;

        var result = await patchTool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("error").GetString().Should().Contain("set_agent_package_draft");
    }

    [Fact]
    public async Task ClearDraft_DeletesFile_AndIsIdempotent()
    {
        var setTool = new SetAgentPackageDraftTool(workspace, recorder);
        await setTool.InvokeAsync(
            JsonDocument.Parse("""{ "package": { "agents": [] } }""").RootElement,
            CancellationToken.None);
        var draftPath = AgentPackageDraftStore.ResolveDraftPath(workspace);
        File.Exists(draftPath).Should().BeTrue();

        var clearTool = new ClearAgentPackageDraftTool(workspace, recorder);
        var first = await clearTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        JsonDocument.Parse(first.ResultJson).RootElement.GetProperty("status").GetString().Should().Be("cleared");
        File.Exists(draftPath).Should().BeFalse();

        var second = await clearTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        JsonDocument.Parse(second.ResultJson).RootElement.GetProperty("status").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task ClearDraft_RefusesWhenAPendingSnapshotExists()
    {
        var setTool = new SetAgentPackageDraftTool(workspace, recorder);
        await setTool.InvokeAsync(
            JsonDocument.Parse("""{ "package": { "agents": [] } }""").RootElement,
            CancellationToken.None);

        var snapshotPayload = JsonNode.Parse("""{ "agents": [] }""")!;
        var snapshotId = await AgentPackageDraftStore.WriteSnapshotAsync(workspace, snapshotPayload, CancellationToken.None);
        AgentPackageDraftStore.HasPendingSnapshots(workspace).Should().BeTrue();

        var clearTool = new ClearAgentPackageDraftTool(workspace, recorder);
        var result = await clearTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        result.IsError.Should().BeTrue();
        JsonDocument.Parse(result.ResultJson).RootElement.GetProperty("error").GetString().Should().Contain("Save chip");

        File.Exists(AgentPackageDraftStore.ResolveDraftPath(workspace)).Should().BeTrue();
        File.Exists(AgentPackageDraftStore.ResolveSnapshotPath(workspace, snapshotId)).Should().BeTrue();

        AgentPackageDraftStore.DeleteSnapshot(workspace, snapshotId);
        var afterApply = await clearTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        JsonDocument.Parse(afterApply.ResultJson).RootElement.GetProperty("status").GetString().Should().Be("cleared");
    }

    [Fact]
    public async Task SetDraft_RejectsRedactionPlaceholder()
    {
        // The redaction allowlist now includes set_agent_package_draft; the model that copies
        // its own redacted prior emission would otherwise overwrite the draft with the stub.
        var tool = new SetAgentPackageDraftTool(workspace, recorder);
        var args = JsonDocument.Parse("""
        {
          "package": {
            "_redacted": true,
            "_doNotCopy": "...",
            "sha256": "deadbeef",
            "sizeBytes": 200,
            "summary": { "agentCount": 1 }
          }
        }
        """).RootElement;

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        JsonDocument.Parse(result.ResultJson).RootElement.GetProperty("error").GetString()
            .Should().Contain("redaction placeholder");
        File.Exists(AgentPackageDraftStore.ResolveDraftPath(workspace)).Should().BeFalse();
    }

    [Fact]
    public void Factory_WithoutWorkspace_StillProvidesSaveTool()
    {
        var factory = new AgentDraftAssistantToolFactory(new StubAgentImporter(), recorder);
        var tools = factory.Build(conversationWorkspace: null);

        tools.Should().ContainSingle(t => t.Name == "save_agent_package");
        tools.Should().NotContain(t => t.Name == "set_agent_package_draft");
    }

    [Fact]
    public void Factory_WithWorkspace_ProvidesAllFiveTools()
    {
        var factory = new AgentDraftAssistantToolFactory(new StubAgentImporter(), recorder);
        var tools = factory.Build(workspace);

        tools.Select(t => t.Name).Should().BeEquivalentTo(new[]
        {
            "save_agent_package",
            "set_agent_package_draft",
            "get_agent_package_draft",
            "patch_agent_package_draft",
            "clear_agent_package_draft",
        });
    }

    [Fact]
    public async Task SetDraft_RecordsAgentDraftArtifact_AndSupersedesPriorByName()
    {
        var setTool = new SetAgentPackageDraftTool(workspace, recorder);
        var args = JsonDocument.Parse("""{ "package": { "agents": [] } }""").RootElement;

        await setTool.InvokeAsync(args, CancellationToken.None);
        await setTool.InvokeAsync(args, CancellationToken.None);

        recorder.RecordedEvents.Should().HaveCount(2);
        recorder.RecordedEvents.Should().AllSatisfy(e =>
        {
            e.ConversationId.Should().Be(workspace.CorrelationId);
            e.Kind.Should().Be(ArtifactEventKind.AgentPackageDraft);
            e.Name.Should().Be(AgentPackageDraftStore.DraftFileName);
            e.RelativePath.Should().Be(AgentPackageDraftStore.DraftFileName);
            e.SnapshotId.Should().BeNull();
            e.SupersedesPriorByName.Should().BeTrue();
            e.SummaryJson.Should().NotBeNullOrEmpty();
        });
    }

    [Fact]
    public async Task ClearDraft_RefusalPath_DoesNotCallClearByName()
    {
        var setTool = new SetAgentPackageDraftTool(workspace, recorder);
        await setTool.InvokeAsync(
            JsonDocument.Parse("""{ "package": { "agents": [] } }""").RootElement,
            CancellationToken.None);
        await AgentPackageDraftStore.WriteSnapshotAsync(
            workspace,
            JsonNode.Parse("""{ "agents": [] }""")!,
            CancellationToken.None);

        recorder.ClearByNameCalls.Clear();

        var clearTool = new ClearAgentPackageDraftTool(workspace, recorder);
        var result = await clearTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        result.IsError.Should().BeTrue();

        recorder.ClearByNameCalls.Should().BeEmpty();
    }

    /// <summary>
    /// Same shape as the workflow test fake — captures every recorder call so individual
    /// tests can inspect the (kind, name, supersedesPriorByName) tuples.
    /// </summary>
    private sealed class TestArtifactRecorder : IArtifactRecorder
    {
        public sealed record RecordedCall(
            Guid ConversationId,
            ArtifactEventKind Kind,
            string Name,
            string RelativePath,
            Guid? SnapshotId,
            string? SummaryJson,
            bool SupersedesPriorByName);

        public sealed record ClearByNameCall(Guid ConversationId, string Name);

        public List<RecordedCall> RecordedEvents { get; } = new();
        public List<ClearByNameCall> ClearByNameCalls { get; } = new();
        public List<Guid> SnapshotsExpired { get; } = new();

        public Task<AssistantArtifactEvent> RecordAsync(
            Guid conversationId,
            ArtifactEventKind kind,
            string name,
            string relativePath,
            Guid? snapshotId,
            string? summaryJson,
            bool supersedesPriorByName,
            CancellationToken cancellationToken = default)
        {
            RecordedEvents.Add(new RecordedCall(
                conversationId, kind, name, relativePath, snapshotId, summaryJson, supersedesPriorByName));
            var synthetic = new AssistantArtifactEvent(
                Id: Guid.NewGuid(),
                ConversationId: conversationId,
                MessageId: null,
                Sequence: RecordedEvents.Count,
                Kind: kind,
                Name: name,
                RelativePath: relativePath,
                SnapshotId: snapshotId,
                SummaryJson: summaryJson,
                SupersededByEventId: null,
                ExpiredAtUtc: null,
                CreatedAtUtc: DateTime.UtcNow);
            return Task.FromResult(synthetic);
        }

        public Task<int> ClearByNameAsync(Guid conversationId, string name, CancellationToken cancellationToken = default)
        {
            ClearByNameCalls.Add(new ClearByNameCall(conversationId, name));
            return Task.FromResult(1);
        }

        public Task<int> MarkSnapshotExpiredAsync(Guid snapshotId, CancellationToken cancellationToken = default)
        {
            SnapshotsExpired.Add(snapshotId);
            return Task.FromResult(1);
        }
    }

    private sealed class StubAgentImporter : IAgentPackageImporter
    {
        public Task<WorkflowPackageImportPreview> PreviewAsync(AgentPackage package, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<WorkflowPackageImportPreview> PreviewAsync(
            AgentPackage package,
            IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<WorkflowPackageImportApplyResult> ApplyAsync(AgentPackage package, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<WorkflowPackageImportApplyResult> ApplyAsync(
            AgentPackage package,
            IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
