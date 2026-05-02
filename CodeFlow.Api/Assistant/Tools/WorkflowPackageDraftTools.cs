using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Runtime;
using Json.Patch;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Per-conversation draft store for in-progress workflow packages. The four tools
/// (<see cref="SetWorkflowPackageDraftTool"/>, <see cref="GetWorkflowPackageDraftTool"/>,
/// <see cref="PatchWorkflowPackageDraftTool"/>, <see cref="ClearWorkflowPackageDraftTool"/>)
/// all read/write a single canonical file (<see cref="DraftFileName"/>) under the
/// conversation's per-chat workspace dir. Save then becomes a zero-arg call to
/// <c>save_workflow_package</c> that reads the draft from disk — the LLM never re-emits
/// the full payload during refinement, which is the cost the user wanted eliminated.
/// </summary>
public static class WorkflowPackageDraftStore
{
    public const string DraftFileName = "draft.cf-workflow-package.json";
    private const string SnapshotPrefix = "snapshot-";
    private const string SnapshotSuffix = ".cf-workflow-package.json";

    public static string ResolveDraftPath(ToolWorkspaceContext workspace) =>
        Path.Combine(workspace.RootPath, DraftFileName);

    public static async Task<JsonNode?> ReadAsync(ToolWorkspaceContext workspace, CancellationToken cancellationToken)
    {
        var path = ResolveDraftPath(workspace);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public static async Task WriteAsync(
        ToolWorkspaceContext workspace,
        JsonNode payload,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workspace.RootPath);
        var path = ResolveDraftPath(workspace);
        // Write through a temp file + rename so a partial write doesn't corrupt the draft on
        // a crash mid-write. Same pattern the workspace host tools use.
        var tempPath = path + ".writing";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                payload,
                AssistantToolJson.SerializerOptions,
                cancellationToken);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    public static bool Delete(ToolWorkspaceContext workspace)
    {
        var path = ResolveDraftPath(workspace);
        if (!File.Exists(path))
        {
            return false;
        }
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Snapshot the validated draft to an immutable per-save file in the same workspace dir
    /// so the user-confirmation chip can apply the EXACT bytes the LLM (and the user) saw at
    /// preview time, even if the live <c>draft.cf-workflow-package.json</c> file is patched or
    /// overwritten before the user clicks Save. Returns the snapshot id (a fresh GUID); the
    /// chip carries it through to <c>apply-from-draft</c> which loads from this snapshot.
    /// <para/>
    /// Apply must call <see cref="DeleteSnapshot"/> on success so the dir doesn't accumulate
    /// abandoned snapshots from cancelled or replaced chips.
    /// </summary>
    public static async Task<Guid> WriteSnapshotAsync(
        ToolWorkspaceContext workspace,
        JsonNode payload,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workspace.RootPath);
        var snapshotId = Guid.NewGuid();
        var path = ResolveSnapshotPath(workspace, snapshotId);
        var tempPath = path + ".writing";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                payload,
                AssistantToolJson.SerializerOptions,
                cancellationToken);
        }
        File.Move(tempPath, path, overwrite: true);
        return snapshotId;
    }

    /// <summary>
    /// Resolves the on-disk path for a per-save snapshot. The snapshot file naming uses a
    /// fixed prefix + suffix and a GUID body so concurrent saves never collide and a stray
    /// path component (e.g. <c>"../../etc/passwd"</c>) gets rejected at the
    /// <see cref="Guid.TryParse(string?, out Guid)"/> guard inside the apply endpoint.
    /// </summary>
    public static string ResolveSnapshotPath(ToolWorkspaceContext workspace, Guid snapshotId) =>
        Path.Combine(workspace.RootPath, $"{SnapshotPrefix}{snapshotId:N}{SnapshotSuffix}");

    public static bool DeleteSnapshot(ToolWorkspaceContext workspace, Guid snapshotId)
    {
        var path = ResolveSnapshotPath(workspace, snapshotId);
        if (!File.Exists(path))
        {
            return false;
        }
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Build a small JSON summary of the draft for tool results. Returns enough metadata
    /// (entry-point, workflow keys, node + edge counts) for the LLM to reason about what's
    /// in the draft without re-receiving the full payload.
    /// </summary>
    public static JsonObject Summarize(JsonNode draft, long sizeBytes)
    {
        var summary = new JsonObject
        {
            ["sizeBytes"] = sizeBytes,
        };

        if (draft is JsonObject root)
        {
            if (root["entryPoint"] is JsonObject entry)
            {
                summary["entryPoint"] = JsonNode.Parse(entry.ToJsonString())!;
            }

            if (root["workflows"] is JsonArray workflows)
            {
                var workflowSummaries = new JsonArray();
                foreach (var workflowNode in workflows)
                {
                    if (workflowNode is not JsonObject workflow) continue;
                    var nodes = workflow["nodes"] as JsonArray;
                    var edges = workflow["edges"] as JsonArray;
                    workflowSummaries.Add(new JsonObject
                    {
                        ["key"] = workflow["key"]?.GetValue<string>(),
                        ["version"] = workflow["version"]?.GetValue<int>(),
                        ["nodeCount"] = nodes?.Count ?? 0,
                        ["edgeCount"] = edges?.Count ?? 0,
                    });
                }
                summary["workflows"] = workflowSummaries;
            }

            if (root["agents"] is JsonArray agents)
            {
                summary["agentCount"] = agents.Count;
            }

            if (root["roles"] is JsonArray roles)
            {
                summary["roleCount"] = roles.Count;
            }
        }

        return summary;
    }
}

/// <summary>
/// Writes a workflow package to the conversation's draft slot. Used by the assistant to park
/// an in-progress package once so it doesn't have to re-emit it on every refinement turn.
/// </summary>
public sealed class SetWorkflowPackageDraftTool : IAssistantTool
{
    private readonly ToolWorkspaceContext workspace;

    public SetWorkflowPackageDraftTool(ToolWorkspaceContext workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        this.workspace = workspace;
    }

    public string Name => "set_workflow_package_draft";

    public string Description =>
        "Save a workflow package as the conversation's working draft (overwrites any existing draft). " +
        "Returns a small summary; the full package is not echoed back. Use this once after assembling " +
        "a package, then iterate with `patch_workflow_package_draft`. When you're ready to save to the " +
        "library, call `save_workflow_package` with NO arguments — it will read this draft from disk. " +
        "This avoids re-emitting the full package on every refinement turn.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""package"": {
                ""type"": ""object"",
                ""description"": ""The full workflow package to store as the draft. Same shape as `save_workflow_package` accepts.""
            }
        },
        ""required"": [""package""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("package", out var packageElement)
            || packageElement.ValueKind != JsonValueKind.Object)
        {
            return Error("Argument `package` is required and must be an object.");
        }

        if (WorkflowPackageRedaction.IsRedactionPlaceholder(packageElement))
        {
            return Error(
                "The `package` argument is a redaction placeholder, not a real workflow package. "
                + "The redacted shape `{\"_redacted\": true, \"sha256\": ..., \"summary\": ...}` "
                + "is what your prior tool_use Inputs are replaced with in your transcript history "
                + "to save tokens — it is NOT a callable input. Re-emit the actual workflow-package "
                + "JSON, or call `get_workflow_package_draft` to read the current draft and "
                + "`patch_workflow_package_draft` to apply incremental edits.");
        }

        JsonNode? packageNode;
        try
        {
            packageNode = JsonNode.Parse(packageElement.GetRawText());
        }
        catch (JsonException ex)
        {
            return Error($"Could not parse `package` as JSON: {ex.Message}");
        }

        if (packageNode is null)
        {
            return Error("Argument `package` deserialized to null.");
        }

        await WorkflowPackageDraftStore.WriteAsync(workspace, packageNode, cancellationToken);
        var info = new FileInfo(WorkflowPackageDraftStore.ResolveDraftPath(workspace));
        var summary = WorkflowPackageDraftStore.Summarize(packageNode, info.Length);

        var payload = new JsonObject
        {
            ["status"] = "saved",
            ["path"] = WorkflowPackageDraftStore.DraftFileName,
            ["summary"] = summary,
            ["message"] = "Draft saved. To iterate, call `patch_workflow_package_draft` with JSON Patch operations. To save to the library, call `save_workflow_package` with no arguments.",
        };
        return new AssistantToolResult(payload.ToJsonString(AssistantToolJson.SerializerOptions));
    }

    private static AssistantToolResult Error(string message)
    {
        var payload = new JsonObject { ["error"] = message };
        return new AssistantToolResult(payload.ToJsonString(AssistantToolJson.SerializerOptions), IsError: true);
    }
}

/// <summary>
/// Reads the conversation's current draft package back. Useful when the assistant needs to
/// inspect the current draft state (e.g. to decide what to patch) without keeping a full copy
/// in its context.
/// </summary>
public sealed class GetWorkflowPackageDraftTool : IAssistantTool
{
    private readonly ToolWorkspaceContext workspace;

    public GetWorkflowPackageDraftTool(ToolWorkspaceContext workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        this.workspace = workspace;
    }

    public string Name => "get_workflow_package_draft";

    public string Description =>
        "Read the conversation's current workflow package draft. Returns the full package object, " +
        "or `status: \"not_found\"` if no draft has been saved yet. Use this when you need to see " +
        "the current draft state — usually to decide what to patch.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {},
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var draft = await WorkflowPackageDraftStore.ReadAsync(workspace, cancellationToken);
        if (draft is null)
        {
            var notFound = new JsonObject
            {
                ["status"] = "not_found",
                ["message"] = "No draft package has been saved for this conversation. Call `set_workflow_package_draft` first.",
            };
            return new AssistantToolResult(notFound.ToJsonString(AssistantToolJson.SerializerOptions));
        }

        var payload = new JsonObject
        {
            ["status"] = "ok",
            ["package"] = draft,
        };
        return new AssistantToolResult(payload.ToJsonString(AssistantToolJson.SerializerOptions));
    }
}

/// <summary>
/// Applies an RFC 6902 JSON Patch to the conversation's draft package. Each operation has
/// <c>op</c> (add | remove | replace | move | copy | test), <c>path</c> (a JSON Pointer like
/// <c>/workflows/0/edges/-</c>), and a <c>value</c> for add/replace/test. This is the cheap
/// edit path: instead of re-emitting the full package to fix a port name or add an edge, the
/// assistant emits a small operation array.
/// </summary>
public sealed class PatchWorkflowPackageDraftTool : IAssistantTool
{
    private readonly ToolWorkspaceContext workspace;

    public PatchWorkflowPackageDraftTool(ToolWorkspaceContext workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        this.workspace = workspace;
    }

    public string Name => "patch_workflow_package_draft";

    public string Description =>
        "Apply RFC 6902 JSON Patch operations to the conversation's draft package in-place. " +
        "Each op is `{ \"op\": \"add\"|\"remove\"|\"replace\"|\"move\"|\"copy\"|\"test\", " +
        "\"path\": \"/workflows/0/edges/-\", \"value\": ... }`. Use `/-` as the array index to " +
        "append. This is far cheaper than re-emitting the full package via " +
        "`set_workflow_package_draft` for small edits (adding an edge, swapping a port name, " +
        "tweaking maxRoundsPerRound). Returns the updated draft summary on success or an error " +
        "describing which operation failed.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""operations"": {
                ""type"": ""array"",
                ""description"": ""Array of RFC 6902 JSON Patch operations."",
                ""items"": {
                    ""type"": ""object"",
                    ""properties"": {
                        ""op"": { ""type"": ""string"", ""enum"": [""add"", ""remove"", ""replace"", ""move"", ""copy"", ""test""] },
                        ""path"": { ""type"": ""string"" },
                        ""from"": { ""type"": ""string"" },
                        ""value"": {}
                    },
                    ""required"": [""op"", ""path""]
                }
            }
        },
        ""required"": [""operations""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("operations", out var opsElement)
            || opsElement.ValueKind != JsonValueKind.Array)
        {
            return Error("Argument `operations` is required and must be an array of JSON Patch operations.");
        }

        var draft = await WorkflowPackageDraftStore.ReadAsync(workspace, cancellationToken);
        if (draft is null)
        {
            return Error("No draft package has been saved for this conversation. Call `set_workflow_package_draft` first.");
        }

        JsonPatch patch;
        try
        {
            patch = JsonSerializer.Deserialize<JsonPatch>(opsElement.GetRawText(), AssistantToolJson.SerializerOptions)
                ?? throw new JsonException("operations deserialized to null");
        }
        catch (JsonException ex)
        {
            return Error($"Could not parse `operations` as JSON Patch: {ex.Message}");
        }

        // JsonPatch.Net operates on JsonNode immutably; pass our parsed draft and capture the
        // result. Failed ops surface a non-success result with an Error string.
        PatchResult patchResult;
        try
        {
            patchResult = patch.Apply(draft);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error($"Failed to apply JSON Patch: {ex.Message}");
        }

        if (!patchResult.IsSuccess)
        {
            return Error(
                $"JSON Patch operation failed: {patchResult.Error ?? "unknown error"}. "
                + "No changes were written to disk.");
        }

        var updated = patchResult.Result ?? throw new InvalidOperationException(
            "JsonPatch.Apply succeeded but produced a null result; this is a library invariant violation.");

        await WorkflowPackageDraftStore.WriteAsync(workspace, updated, cancellationToken);
        var info = new FileInfo(WorkflowPackageDraftStore.ResolveDraftPath(workspace));
        var summary = WorkflowPackageDraftStore.Summarize(updated, info.Length);

        var payload = new JsonObject
        {
            ["status"] = "patched",
            ["operationsApplied"] = opsElement.GetArrayLength(),
            ["summary"] = summary,
            ["message"] = "Draft updated. Call `save_workflow_package` (no arguments) when ready to save to the library.",
        };
        return new AssistantToolResult(payload.ToJsonString(AssistantToolJson.SerializerOptions));
    }

    private static AssistantToolResult Error(string message)
    {
        var payload = new JsonObject { ["error"] = message };
        return new AssistantToolResult(payload.ToJsonString(AssistantToolJson.SerializerOptions), IsError: true);
    }
}

/// <summary>
/// Deletes the conversation's draft package. Useful housekeeping after a successful save or
/// when the user pivots to a fresh design.
/// </summary>
public sealed class ClearWorkflowPackageDraftTool : IAssistantTool
{
    private readonly ToolWorkspaceContext workspace;

    public ClearWorkflowPackageDraftTool(ToolWorkspaceContext workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        this.workspace = workspace;
    }

    public string Name => "clear_workflow_package_draft";

    public string Description =>
        "Delete the conversation's draft package. Returns `status: \"cleared\"` if a draft was " +
        "deleted or `status: \"not_found\"` if there was none. Useful after a successful save or " +
        "when starting a fresh design.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {},
        ""additionalProperties"": false
    }");

    public Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var deleted = WorkflowPackageDraftStore.Delete(workspace);
        var payload = new JsonObject
        {
            ["status"] = deleted ? "cleared" : "not_found",
        };
        return Task.FromResult(new AssistantToolResult(payload.ToJsonString(AssistantToolJson.SerializerOptions)));
    }
}

/// <summary>
/// Per-turn factory: always builds <see cref="SaveWorkflowPackageTool"/>; additionally builds
/// the four draft tools when the conversation has a writable workspace. The save tool is
/// constructed here (rather than registered in DI) so it can be workspace-aware: when a
/// workspace is available, it accepts a zero-arg form that reads the conversation's draft
/// from disk instead of forcing the LLM to re-emit the full package payload.
/// </summary>
public sealed class WorkflowDraftAssistantToolFactory
{
    private readonly IWorkflowPackageImporter importer;

    public WorkflowDraftAssistantToolFactory(IWorkflowPackageImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        this.importer = importer;
    }

    public IReadOnlyList<IAssistantTool> Build(ToolWorkspaceContext? workspace)
    {
        var tools = new List<IAssistantTool>
        {
            new SaveWorkflowPackageTool(importer, workspace),
        };

        if (workspace is not null)
        {
            tools.Add(new SetWorkflowPackageDraftTool(workspace));
            tools.Add(new GetWorkflowPackageDraftTool(workspace));
            tools.Add(new PatchWorkflowPackageDraftTool(workspace));
            tools.Add(new ClearWorkflowPackageDraftTool(workspace));
        }

        return tools;
    }
}

/// <summary>
/// Result handed back when the importer applies a draft from disk. Distinguished from
/// <see cref="WorkflowPackageImportApplyResult"/> only by the source field, kept identical
/// otherwise so the existing chip success-path UI works unchanged.
/// </summary>
public sealed record ApplyFromDraftRequest(Guid ConversationId);
