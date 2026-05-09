using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.Assistant.Artifacts;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using Json.Patch;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// AP-4 (sc-835): writes an agent package to the conversation's draft slot. Used by the
/// assistant to park an in-progress package once so it doesn't have to re-emit it on every
/// refinement turn. Sibling of <see cref="SetWorkflowPackageDraftTool"/>.
/// </summary>
public sealed class SetAgentPackageDraftTool : IAssistantTool
{
    private readonly ToolWorkspaceContext workspace;
    private readonly IArtifactRecorder artifactRecorder;

    public SetAgentPackageDraftTool(ToolWorkspaceContext workspace, IArtifactRecorder artifactRecorder)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(artifactRecorder);
        this.workspace = workspace;
        this.artifactRecorder = artifactRecorder;
    }

    public string Name => "set_agent_package_draft";

    public string Description =>
        "Save an agent package as the conversation's working draft. **Use ONCE** when first " +
        "assembling a package — re-calling this tool to make incremental edits wastes input " +
        "tokens for changes that fit in a few JSON Patch ops. The cheap edit path is " +
        "`patch_agent_package_draft({ operations: [...] })`; call `get_agent_package_draft()` " +
        "first if you need to see the current state to compute paths. When you're ready to save " +
        "to the library, call `save_agent_package` with NO arguments — it reads this draft from " +
        "disk. Returns a small summary; the package is not echoed back. Overwriting an existing " +
        "draft is allowed but flagged in the result.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""package"": {
                ""type"": ""object"",
                ""description"": ""The full agent package to store as the draft. Same shape as `save_agent_package` accepts (a codeflow.agent-package.v1 document).""
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
                "The `package` argument is a redaction placeholder, not a real agent package. "
                + "The shape `{\"_redacted\": true, \"sha256\": ..., \"summary\": ...}` is the "
                + "transcript-only stub the runtime substitutes for prior package emissions to "
                + "save tokens; the original draft is intact on disk. **Recovery procedure** "
                + "(do this in order, do not loop): "
                + "1) Call `get_agent_package_draft()` to surface the current bytes. "
                + "2) Compute the targeted JSON Patch ops the user's request needs. "
                + "3) Call `patch_agent_package_draft({ operations: [...] })` — DO NOT "
                + "re-emit the full package via `set_agent_package_draft` for an edit, "
                + "that's the expensive path you just bounced off. "
                + "Re-emitting is only correct when starting a fresh design, not when iterating "
                + "on an existing draft.");
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

        var wasOverwrite = File.Exists(AgentPackageDraftStore.ResolveDraftPath(workspace));

        await AgentPackageDraftStore.WriteAsync(workspace, packageNode, cancellationToken);
        var info = new FileInfo(AgentPackageDraftStore.ResolveDraftPath(workspace));
        var summary = AgentPackageDraftStore.Summarize(packageNode, info.Length);

        // Register the draft as an artifact event so the chat UI can surface a downloadable
        // pill. workspace.CorrelationId == conversationId for the assistant workspace provider.
        await artifactRecorder.RecordAsync(
            conversationId: workspace.CorrelationId,
            kind: ArtifactEventKind.AgentPackageDraft,
            name: AgentPackageDraftStore.DraftFileName,
            relativePath: AgentPackageDraftStore.DraftFileName,
            snapshotId: null,
            summaryJson: summary.ToJsonString(AssistantToolJson.SerializerOptions),
            supersedesPriorByName: true,
            cancellationToken: cancellationToken);

        var message = wasOverwrite
            ? "Draft REPLACED in full. If this turn's change was small (an output port, a role " +
              "key, a scalar), `patch_agent_package_draft({ operations: [...] })` would have " +
              "cost a few hundred tokens vs the full re-emit. Future small edits MUST use the " +
              "patch path; call `get_agent_package_draft()` first if you need to see the " +
              "current state to compute paths. To save to the library, call `save_agent_package` " +
              "with no arguments."
            : "Draft saved. Future edits should use `patch_agent_package_draft` (cheap) rather " +
              "than another `set_agent_package_draft` (full re-emit). To save to the library, " +
              "call `save_agent_package` with no arguments.";

        var payload = new JsonObject
        {
            ["status"] = "saved",
            ["wasOverwrite"] = wasOverwrite,
            ["path"] = AgentPackageDraftStore.DraftFileName,
            ["summary"] = summary,
            ["message"] = message,
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
/// AP-4: reads the conversation's current agent-package draft back. Useful when the assistant
/// needs to inspect the current draft state (e.g. to decide what to patch) without keeping a
/// full copy in its context.
/// </summary>
public sealed class GetAgentPackageDraftTool : IAssistantTool
{
    private readonly ToolWorkspaceContext workspace;

    public GetAgentPackageDraftTool(ToolWorkspaceContext workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        this.workspace = workspace;
    }

    public string Name => "get_agent_package_draft";

    public string Description =>
        "Read the conversation's current agent package draft. Returns the full package object, " +
        "or `status: \"not_found\"` if no draft has been saved yet. Use this when you need to " +
        "see the current draft state — usually to decide what to patch.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {},
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var draft = await AgentPackageDraftStore.ReadAsync(workspace, cancellationToken);
        if (draft is null)
        {
            var notFound = new JsonObject
            {
                ["status"] = "not_found",
                ["message"] = "No draft package has been saved for this conversation. Call `set_agent_package_draft` first.",
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
/// AP-4: applies an RFC 6902 JSON Patch to the conversation's agent-package draft. Each
/// operation has <c>op</c> (add | remove | replace | move | copy | test), <c>path</c> (a
/// JSON Pointer like <c>/agents/0/config/systemPrompt</c>), and a <c>value</c> for
/// add/replace/test. This is the cheap edit path: instead of re-emitting the full package
/// to fix a system prompt or add a role key, the assistant emits a small operation array.
/// </summary>
public sealed class PatchAgentPackageDraftTool : IAssistantTool
{
    private readonly ToolWorkspaceContext workspace;
    private readonly IArtifactRecorder artifactRecorder;

    public PatchAgentPackageDraftTool(ToolWorkspaceContext workspace, IArtifactRecorder artifactRecorder)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(artifactRecorder);
        this.workspace = workspace;
        this.artifactRecorder = artifactRecorder;
    }

    public string Name => "patch_agent_package_draft";

    public string Description =>
        "Apply RFC 6902 JSON Patch operations to the conversation's agent-package draft in-place. " +
        "Each op is `{ \"op\": \"add\"|\"remove\"|\"replace\"|\"move\"|\"copy\"|\"test\", " +
        "\"path\": \"/agents/0/config/systemPrompt\", \"value\": ... }`. Use `/-` as the array " +
        "index to append. This is far cheaper than re-emitting the full package via " +
        "`set_agent_package_draft` for small edits (tweaking a system prompt, swapping a role " +
        "grant, adding a tag). Returns the updated draft summary on success or an error " +
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

        var draft = await AgentPackageDraftStore.ReadAsync(workspace, cancellationToken);
        if (draft is null)
        {
            return Error("No draft package has been saved for this conversation. Call `set_agent_package_draft` first.");
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

        await AgentPackageDraftStore.WriteAsync(workspace, updated, cancellationToken);
        var info = new FileInfo(AgentPackageDraftStore.ResolveDraftPath(workspace));
        var summary = AgentPackageDraftStore.Summarize(updated, info.Length);

        await artifactRecorder.RecordAsync(
            conversationId: workspace.CorrelationId,
            kind: ArtifactEventKind.AgentPackageDraft,
            name: AgentPackageDraftStore.DraftFileName,
            relativePath: AgentPackageDraftStore.DraftFileName,
            snapshotId: null,
            summaryJson: summary.ToJsonString(AssistantToolJson.SerializerOptions),
            supersedesPriorByName: true,
            cancellationToken: cancellationToken);

        var payload = new JsonObject
        {
            ["status"] = "patched",
            ["operationsApplied"] = opsElement.GetArrayLength(),
            ["summary"] = summary,
            ["message"] = "Draft updated. Call `save_agent_package` (no arguments) when ready to save to the library.",
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
/// AP-4: deletes the conversation's agent-package draft. User-initiated only — see the tool
/// description. Refuses to clear while a Save chip is still pending the user's click.
/// </summary>
public sealed class ClearAgentPackageDraftTool : IAssistantTool
{
    private readonly ToolWorkspaceContext workspace;
    private readonly IArtifactRecorder artifactRecorder;

    public ClearAgentPackageDraftTool(ToolWorkspaceContext workspace, IArtifactRecorder artifactRecorder)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(artifactRecorder);
        this.workspace = workspace;
        this.artifactRecorder = artifactRecorder;
    }

    public string Name => "clear_agent_package_draft";

    public string Description =>
        "Delete the conversation's agent-package draft. USER-INITIATED ONLY: call this only " +
        "when the user explicitly tells you they are done with the current draft and want to " +
        "start a fresh design. Do NOT call it after `save_agent_package` returns `preview_ok` — " +
        "that result means the Save chip is awaiting the user's click, NOT that the save " +
        "completed. The actual save lands when the user clicks the chip; iterating further " +
        "(patch + save again) is expected and requires the draft to still be on disk. The tool " +
        "refuses to clear while a Save chip is still pending and returns `status: \"cleared\"` " +
        "/ `\"not_found\"` otherwise.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {},
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (AgentPackageDraftStore.HasPendingSnapshots(workspace))
        {
            var refusal = new JsonObject
            {
                ["error"] =
                    "A Save chip is still awaiting the user's click for this draft. "
                    + "Do not clear the draft yet — `preview_ok` means \"validated, awaiting user confirmation\", "
                    + "NOT \"saved\". Wait for the user to either click Save (after which the apply endpoint "
                    + "consumes the snapshot) or to explicitly tell you they are done with this draft. "
                    + "If they want to iterate further, call `patch_agent_package_draft` instead.",
            };
            return new AssistantToolResult(
                refusal.ToJsonString(AssistantToolJson.SerializerOptions),
                IsError: true);
        }

        var deleted = AgentPackageDraftStore.Delete(workspace);

        if (deleted)
        {
            await artifactRecorder.ClearByNameAsync(
                conversationId: workspace.CorrelationId,
                name: AgentPackageDraftStore.DraftFileName,
                cancellationToken: cancellationToken);
        }

        var payload = new JsonObject
        {
            ["status"] = deleted ? "cleared" : "not_found",
        };
        return new AssistantToolResult(payload.ToJsonString(AssistantToolJson.SerializerOptions));
    }
}

/// <summary>
/// AP-4: per-turn factory for the agent-package authoring surface. Always builds
/// <see cref="SaveAgentPackageTool"/>; additionally builds the four draft tools when the
/// conversation has a writable workspace. Mirrors <see cref="WorkflowDraftAssistantToolFactory"/>.
/// <para/>
/// The workspace argument is the *conversation* workspace, not the active workspace — see
/// <see cref="AgentPackageDraftStore"/>'s class-level docs for the rationale.
/// </summary>
public sealed class AgentDraftAssistantToolFactory
{
    private readonly IAgentPackageImporter importer;
    private readonly IArtifactRecorder artifactRecorder;

    public AgentDraftAssistantToolFactory(
        IAgentPackageImporter importer,
        IArtifactRecorder artifactRecorder)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(artifactRecorder);
        this.importer = importer;
        this.artifactRecorder = artifactRecorder;
    }

    public IReadOnlyList<IAssistantTool> Build(ToolWorkspaceContext? conversationWorkspace)
    {
        var tools = new List<IAssistantTool>
        {
            new SaveAgentPackageTool(importer, conversationWorkspace, artifactRecorder),
        };

        if (conversationWorkspace is not null)
        {
            tools.Add(new SetAgentPackageDraftTool(conversationWorkspace, artifactRecorder));
            tools.Add(new GetAgentPackageDraftTool(conversationWorkspace));
            tools.Add(new PatchAgentPackageDraftTool(conversationWorkspace, artifactRecorder));
            tools.Add(new ClearAgentPackageDraftTool(conversationWorkspace, artifactRecorder));
        }

        return tools;
    }
}
