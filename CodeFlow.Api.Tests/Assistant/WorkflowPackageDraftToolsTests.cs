using System.Text.Json;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Runtime;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Unit tests for the four workflow-package draft tools. These exercise the on-disk store,
/// JSON Patch application, and the not-found / invalid-input branches without spinning up the
/// full API host (the tools are purely workspace-IO + JSON manipulation).
/// </summary>
public sealed class WorkflowPackageDraftToolsTests : IDisposable
{
    private readonly string tempDir;
    private readonly ToolWorkspaceContext workspace;

    public WorkflowPackageDraftToolsTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "cf-draft-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        workspace = new ToolWorkspaceContext(Guid.NewGuid(), tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SetDraft_WritesPackageToDisk_AndReturnsSummary()
    {
        var tool = new SetWorkflowPackageDraftTool(workspace);
        var args = JsonDocument.Parse("""
        {
          "package": {
            "schemaVersion": "codeflow.workflow-package.v1",
            "entryPoint": { "key": "demo", "version": 1 },
            "workflows": [{ "key": "demo", "version": 1, "nodes": [{ "id": "n1" }, { "id": "n2" }], "edges": [{}] }],
            "agents": [{ "key": "writer", "version": 3 }]
          }
        }
        """).RootElement;

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("saved");
        parsed.GetProperty("path").GetString().Should().Be(WorkflowPackageDraftStore.DraftFileName);

        var summary = parsed.GetProperty("summary");
        summary.GetProperty("workflows")[0].GetProperty("nodeCount").GetInt32().Should().Be(2);
        summary.GetProperty("workflows")[0].GetProperty("edgeCount").GetInt32().Should().Be(1);
        summary.GetProperty("agentCount").GetInt32().Should().Be(1);

        // The draft file is on disk and parseable.
        var draftPath = Path.Combine(tempDir, WorkflowPackageDraftStore.DraftFileName);
        File.Exists(draftPath).Should().BeTrue();
    }

    [Fact]
    public async Task GetDraft_AfterSet_ReturnsStoredPackage()
    {
        var setTool = new SetWorkflowPackageDraftTool(workspace);
        var setArgs = JsonDocument.Parse("""
        { "package": { "schemaVersion": "codeflow.workflow-package.v1", "workflows": [{ "key": "x" }] } }
        """).RootElement;
        await setTool.InvokeAsync(setArgs, CancellationToken.None);

        var getTool = new GetWorkflowPackageDraftTool(workspace);
        var result = await getTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("ok");
        parsed.GetProperty("package").GetProperty("workflows")[0].GetProperty("key").GetString().Should().Be("x");
    }

    [Fact]
    public async Task GetDraft_WhenAbsent_ReturnsNotFound()
    {
        var getTool = new GetWorkflowPackageDraftTool(workspace);
        var result = await getTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task PatchDraft_AppliesAddOp_ToAppendEdge()
    {
        // Seed a draft that has an empty edges array; verify a JSON Patch add-with-/- appends.
        var setTool = new SetWorkflowPackageDraftTool(workspace);
        var setArgs = JsonDocument.Parse("""
        { "package": { "workflows": [{ "key": "x", "edges": [] }] } }
        """).RootElement;
        await setTool.InvokeAsync(setArgs, CancellationToken.None);

        var patchTool = new PatchWorkflowPackageDraftTool(workspace);
        var patchArgs = JsonDocument.Parse("""
        {
          "operations": [
            { "op": "add", "path": "/workflows/0/edges/-", "value": { "fromPort": "Completed" } }
          ]
        }
        """).RootElement;
        var result = await patchTool.InvokeAsync(patchArgs, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("patched");
        parsed.GetProperty("operationsApplied").GetInt32().Should().Be(1);

        // Verify the edge actually landed in the on-disk draft.
        var getTool = new GetWorkflowPackageDraftTool(workspace);
        var getResult = await getTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        var pkg = JsonDocument.Parse(getResult.ResultJson).RootElement.GetProperty("package");
        var edges = pkg.GetProperty("workflows")[0].GetProperty("edges");
        edges.GetArrayLength().Should().Be(1);
        edges[0].GetProperty("fromPort").GetString().Should().Be("Completed");
    }

    [Fact]
    public async Task PatchDraft_AppliesReplaceOp_ToScalarField()
    {
        var setTool = new SetWorkflowPackageDraftTool(workspace);
        var setArgs = JsonDocument.Parse("""
        { "package": { "workflows": [{ "key": "x", "maxRoundsPerRound": 1 }] } }
        """).RootElement;
        await setTool.InvokeAsync(setArgs, CancellationToken.None);

        var patchTool = new PatchWorkflowPackageDraftTool(workspace);
        var patchArgs = JsonDocument.Parse("""
        { "operations": [ { "op": "replace", "path": "/workflows/0/maxRoundsPerRound", "value": 7 } ] }
        """).RootElement;
        var result = await patchTool.InvokeAsync(patchArgs, CancellationToken.None);

        result.IsError.Should().BeFalse();

        var getTool = new GetWorkflowPackageDraftTool(workspace);
        var getResult = await getTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        var pkg = JsonDocument.Parse(getResult.ResultJson).RootElement.GetProperty("package");
        pkg.GetProperty("workflows")[0].GetProperty("maxRoundsPerRound").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task PatchDraft_FailingOp_DoesNotMutateDisk()
    {
        var setTool = new SetWorkflowPackageDraftTool(workspace);
        var setArgs = JsonDocument.Parse("""
        { "package": { "workflows": [{ "key": "x", "maxRoundsPerRound": 3 }] } }
        """).RootElement;
        await setTool.InvokeAsync(setArgs, CancellationToken.None);
        var draftPath = Path.Combine(tempDir, WorkflowPackageDraftStore.DraftFileName);
        var beforeBytes = await File.ReadAllBytesAsync(draftPath);

        var patchTool = new PatchWorkflowPackageDraftTool(workspace);
        // 'test' op verifies a value; use a wrong value so the op fails.
        var patchArgs = JsonDocument.Parse("""
        { "operations": [ { "op": "test", "path": "/workflows/0/maxRoundsPerRound", "value": 999 } ] }
        """).RootElement;
        var result = await patchTool.InvokeAsync(patchArgs, CancellationToken.None);

        result.IsError.Should().BeTrue();
        var afterBytes = await File.ReadAllBytesAsync(draftPath);
        afterBytes.Should().BeEquivalentTo(beforeBytes,
            because: "a failing JSON Patch op must not corrupt the on-disk draft");
    }

    [Fact]
    public async Task PatchDraft_WhenNoDraftExists_ReturnsError()
    {
        var patchTool = new PatchWorkflowPackageDraftTool(workspace);
        var patchArgs = JsonDocument.Parse("""
        { "operations": [ { "op": "add", "path": "/foo", "value": "bar" } ] }
        """).RootElement;
        var result = await patchTool.InvokeAsync(patchArgs, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("set_workflow_package_draft");
    }

    [Fact]
    public async Task ClearDraft_DeletesFile_AndIsIdempotent()
    {
        var setTool = new SetWorkflowPackageDraftTool(workspace);
        await setTool.InvokeAsync(
            JsonDocument.Parse("""{ "package": { "k": "v" } }""").RootElement,
            CancellationToken.None);
        var draftPath = Path.Combine(tempDir, WorkflowPackageDraftStore.DraftFileName);
        File.Exists(draftPath).Should().BeTrue();

        var clearTool = new ClearWorkflowPackageDraftTool(workspace);
        var first = await clearTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        var firstParsed = JsonDocument.Parse(first.ResultJson).RootElement;
        firstParsed.GetProperty("status").GetString().Should().Be("cleared");
        File.Exists(draftPath).Should().BeFalse();

        // Idempotent: calling clear again returns not_found rather than throwing.
        var second = await clearTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        var secondParsed = JsonDocument.Parse(second.ResultJson).RootElement;
        secondParsed.GetProperty("status").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task ClearDraft_RefusesWhenAPendingSnapshotExists()
    {
        // Set up: a draft on disk + a pending Save snapshot. This is the exact state save_workflow_package
        // leaves behind after preview_ok — the user has an open Save chip and hasn't clicked it yet.
        var setTool = new SetWorkflowPackageDraftTool(workspace);
        await setTool.InvokeAsync(
            JsonDocument.Parse("""{ "package": { "k": "v" } }""").RootElement,
            CancellationToken.None);
        var draftPath = Path.Combine(tempDir, WorkflowPackageDraftStore.DraftFileName);
        File.Exists(draftPath).Should().BeTrue();

        var snapshotPayload = System.Text.Json.Nodes.JsonNode.Parse("""{ "k": "v" }""")!;
        var snapshotId = await WorkflowPackageDraftStore.WriteSnapshotAsync(
            workspace, snapshotPayload, CancellationToken.None);
        WorkflowPackageDraftStore.HasPendingSnapshots(workspace).Should().BeTrue();

        var clearTool = new ClearWorkflowPackageDraftTool(workspace);
        var result = await clearTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        result.IsError.Should().BeTrue();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("error").GetString().Should().Contain("Save chip");

        // Draft AND snapshot are both still on disk — refusal is a no-op, not a partial wipe.
        File.Exists(draftPath).Should().BeTrue();
        File.Exists(WorkflowPackageDraftStore.ResolveSnapshotPath(workspace, snapshotId)).Should().BeTrue();

        // Once the snapshot is consumed (apply succeeded), clear works again.
        WorkflowPackageDraftStore.DeleteSnapshot(workspace, snapshotId);
        var afterApply = await clearTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        JsonDocument.Parse(afterApply.ResultJson).RootElement.GetProperty("status").GetString().Should().Be("cleared");
    }

    [Fact]
    public async Task SetDraft_RejectsRedactionPlaceholder()
    {
        // CE-3 follow-up: a model that copies its own redacted prior emission would otherwise
        // overwrite the draft with the stub. The tool must refuse the placeholder shape with an
        // explicit error directing the model to emit a real package.
        var tool = new SetWorkflowPackageDraftTool(workspace);
        var args = JsonDocument.Parse("""
        {
          "package": {
            "_redacted": true,
            "_doNotCopy": "...",
            "sha256": "deadbeef",
            "sizeBytes": 200,
            "summary": { "workflowCount": 1, "nodeCount": 3 }
          }
        }
        """).RootElement;

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("error").GetString().Should().Contain("redaction placeholder");

        // No file should have been written.
        File.Exists(WorkflowPackageDraftStore.ResolveDraftPath(workspace)).Should().BeFalse();
    }

    [Fact]
    public void Factory_WithoutWorkspace_StillProvidesSaveTool()
    {
        // The factory must always provide save_workflow_package (so the LLM can save with an
        // inline `package` arg even when no workspace is available); the four draft tools are
        // workspace-gated.
        var factory = new WorkflowDraftAssistantToolFactory(new StubImporter());
        var tools = factory.Build(conversationWorkspace: null);

        tools.Should().ContainSingle(t => t.Name == "save_workflow_package");
        tools.Should().NotContain(t => t.Name == "set_workflow_package_draft");
    }

    [Fact]
    public void Factory_WithWorkspace_ProvidesAllFiveTools()
    {
        var factory = new WorkflowDraftAssistantToolFactory(new StubImporter());
        var tools = factory.Build(workspace);

        tools.Select(t => t.Name).Should().BeEquivalentTo(new[]
        {
            "save_workflow_package",
            "set_workflow_package_draft",
            "get_workflow_package_draft",
            "patch_workflow_package_draft",
            "clear_workflow_package_draft",
        });
    }

    private sealed class StubImporter : CodeFlow.Api.WorkflowPackages.IWorkflowPackageImporter
    {
        public Task<CodeFlow.Api.WorkflowPackages.WorkflowPackageImportPreview> PreviewAsync(
            CodeFlow.Api.WorkflowPackages.WorkflowPackage package,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<CodeFlow.Api.WorkflowPackages.WorkflowPackageImportPreview> PreviewAsync(
            CodeFlow.Api.WorkflowPackages.WorkflowPackage package,
            IReadOnlyDictionary<CodeFlow.Api.WorkflowPackages.WorkflowPackageImportResolutionKey,
                CodeFlow.Api.WorkflowPackages.WorkflowPackageImportResolution>? resolutions,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<CodeFlow.Api.WorkflowPackages.WorkflowPackageImportApplyResult> ApplyAsync(
            CodeFlow.Api.WorkflowPackages.WorkflowPackage package,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<CodeFlow.Api.WorkflowPackages.WorkflowPackageImportApplyResult> ApplyAsync(
            CodeFlow.Api.WorkflowPackages.WorkflowPackage package,
            IReadOnlyDictionary<CodeFlow.Api.WorkflowPackages.WorkflowPackageImportResolutionKey,
                CodeFlow.Api.WorkflowPackages.WorkflowPackageImportResolution>? resolutions,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<CodeFlow.Api.WorkflowPackages.WorkflowPackageValidationResult> ValidateAsync(
            CodeFlow.Api.WorkflowPackages.WorkflowPackage package,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
