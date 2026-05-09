using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.Assistant.Artifacts;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using CodeFlow.Runtime;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// AP-4 (sc-835): lets the assistant offer to save a drafted agent package into the library,
/// gated by a UI-side confirmation chip. Sibling of <see cref="SaveWorkflowPackageTool"/>.
/// </summary>
/// <remarks>
/// The tool itself does NOT mutate. It runs <see cref="IAgentPackageImporter.PreviewAsync"/>
/// to validate self-containment and structural correctness, snapshots the validated draft
/// bytes, and returns the preview verdict. The chat UI detects the tool by name, surfaces a
/// confirmation chip carrying the snapshot id, and on confirm posts to a future
/// <c>POST /api/agents/package/apply-from-draft</c> endpoint (deferred to a follow-up of
/// AP-2; until that lands, the chip can use <c>POST /api/agents/package/apply</c> with the
/// inline package payload as a fallback).
/// <para/>
/// Mirrors the workflow Save tool, minus the separate <c>ValidateAsync</c> step — agent
/// packages have no workflow shape to validate against (no node graph, edges, ports), so
/// admission + per-row equivalence preview already covers the failure modes a workflow
/// validator would catch.
/// </remarks>
public sealed class SaveAgentPackageTool : IAssistantTool
{
    private readonly IAgentPackageImporter importer;
    private readonly ToolWorkspaceContext? workspace;
    private readonly IArtifactRecorder? artifactRecorder;

    /// <summary>
    /// Constructor used by the per-turn factory. <paramref name="workspace"/> is non-null
    /// when the conversation has a writable workspace — in that mode the tool also accepts
    /// a zero-arg invocation and reads the package from <c>draft.cf-agent-package.json</c>
    /// in the workspace, so the LLM doesn't have to re-emit the full payload.
    /// <paramref name="artifactRecorder"/> is null only on the constructor used by the
    /// no-workspace registration in DI (where the snapshot path can't trigger anyway).
    /// </summary>
    public SaveAgentPackageTool(
        IAgentPackageImporter importer,
        ToolWorkspaceContext? workspace = null,
        IArtifactRecorder? artifactRecorder = null)
    {
        ArgumentNullException.ThrowIfNull(importer);
        this.importer = importer;
        this.workspace = workspace;
        this.artifactRecorder = artifactRecorder;
    }

    public string Name => "save_agent_package";

    public string Description =>
        "Validate a drafted agent package against the library and offer to save it. The tool " +
        "runs a preview against the same admission + equivalence rules the import endpoint " +
        "applies; it does NOT save. The chat UI surfaces a 'Save' confirmation chip — only " +
        "the user clicking that chip persists the package. " +
        (workspace is not null
            ? "There are TWO ways to invoke this tool: (1) pass a `package` object to validate " +
              "it directly, OR (2) call with no arguments to validate the conversation's draft " +
              "package saved via `set_agent_package_draft`. Form (2) is strongly preferred for " +
              "refinement loops because the LLM does not have to re-emit the full payload on " +
              "every save attempt. "
            : "Required: `package` (a codeflow.agent-package.v1 document). ") +
        "You do NOT need to embed unchanged dependencies that already exist in the target " +
        "library — the importer resolves any role/skill/MCP server referenced by the agent " +
        "but omitted from the package against the local DB and reports it as a Reuse in the " +
        "preview. Only embed an entity when you're creating it or intentionally bumping it. " +
        "Enum fields accept the canonical PascalCase strings you see in `get_agent` output. " +
        "After invoking this tool, do NOT call it again or take further action; wait for the " +
        "user's next message.";

    public JsonElement InputSchema => AssistantToolJson.Schema(workspace is not null
        ? @"{
        ""type"": ""object"",
        ""properties"": {
            ""package"": {
                ""type"": ""object"",
                ""description"": ""Optional. A codeflow.agent-package.v1 document. Omit to use the conversation's draft (saved via set_agent_package_draft) — preferred for refinement loops to avoid re-emitting the payload.""
            },
            ""note"": {
                ""type"": ""string"",
                ""description"": ""Optional human-facing note explaining why this save is being offered. Echoed back in the result for the chat UI.""
            }
        },
        ""additionalProperties"": false
    }"
        : @"{
        ""type"": ""object"",
        ""properties"": {
            ""package"": {
                ""type"": ""object"",
                ""description"": ""A codeflow.agent-package.v1 document carrying the agent + role/skill/MCP closure.""
            },
            ""note"": {
                ""type"": ""string"",
                ""description"": ""Optional human-facing note explaining why this save is being offered. Echoed back in the result for the chat UI.""
            }
        },
        ""required"": [""package""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        AgentPackage? package;
        var packageSource = "inline";
        // Holds the parsed JSON for the draft path so we can snapshot it after validation
        // succeeds without re-serializing the typed `package` (which would re-order fields).
        JsonNode? draftNodeForSnapshot = null;

        JsonElement packageElement = default;
        var packagePropertyPresent = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("package", out packageElement);

        if (packagePropertyPresent && packageElement.ValueKind == JsonValueKind.Object)
        {
            if (WorkflowPackageRedaction.IsRedactionPlaceholder(packageElement))
            {
                return Error(
                    "The `package` argument is a redaction placeholder, not a real agent package. "
                    + "The redacted shape `{\"_redacted\": true, \"sha256\": ..., \"summary\": ...}` "
                    + "is what your prior tool_use Inputs are replaced with in your transcript history "
                    + "to save tokens — it is NOT a callable input. Either omit the `package` argument "
                    + "to use the conversation's draft (preferred), or re-emit the actual agent-package JSON.");
            }

            try
            {
                package = packageElement.Deserialize<AgentPackage>(AssistantToolJson.SerializerOptions);
            }
            catch (JsonException ex)
            {
                return Error($"Could not parse `package` as an agent package document: {ex.Message}");
            }

            // Stage the inline package as the conversation's draft so it has the same artifact
            // surface the zero-arg path produces — the user can recover the bytes from the rail
            // if the chip is dismissed.
            if (workspace is not null)
            {
                try
                {
                    draftNodeForSnapshot = JsonNode.Parse(packageElement.GetRawText());
                }
                catch (JsonException)
                {
                    draftNodeForSnapshot = null;
                }

                if (draftNodeForSnapshot is not null)
                {
                    try
                    {
                        await AgentPackageDraftStore.WriteAsync(workspace, draftNodeForSnapshot, cancellationToken);

                        if (artifactRecorder is not null)
                        {
                            var draftInfo = new FileInfo(AgentPackageDraftStore.ResolveDraftPath(workspace));
                            var draftSummary = AgentPackageDraftStore.Summarize(draftNodeForSnapshot, draftInfo.Length);
                            await artifactRecorder.RecordAsync(
                                conversationId: workspace.CorrelationId,
                                kind: ArtifactEventKind.AgentPackageDraft,
                                name: AgentPackageDraftStore.DraftFileName,
                                relativePath: AgentPackageDraftStore.DraftFileName,
                                snapshotId: null,
                                summaryJson: draftSummary.ToJsonString(AssistantToolJson.SerializerOptions),
                                supersedesPriorByName: true,
                                cancellationToken: cancellationToken);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Workspace IO failure must not break the in-turn save flow.
                        draftNodeForSnapshot = null;
                    }
                }
            }
        }
        else if (packagePropertyPresent)
        {
            return Error(
                $"Argument `package` is present but its JSON kind is `{packageElement.ValueKind}`. "
                + "It must be an agent package object, or omitted entirely to use the conversation's draft.");
        }
        else if (workspace is not null)
        {
            // Zero-arg form: read the conversation's draft from disk.
            try
            {
                draftNodeForSnapshot = await AgentPackageDraftStore.ReadAsync(workspace, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error($"Failed to read draft package from workspace: {ex.Message}");
            }

            if (draftNodeForSnapshot is null)
            {
                return Error("No draft package has been saved for this conversation. Call `set_agent_package_draft` first, or pass a `package` argument directly.");
            }

            try
            {
                package = draftNodeForSnapshot.Deserialize<AgentPackage>(AssistantToolJson.SerializerOptions);
            }
            catch (JsonException ex)
            {
                return Error($"Could not parse the conversation's draft as an agent package document: {ex.Message}");
            }
            packageSource = "draft";
        }
        else
        {
            return Error("Argument `package` is required and must be an object (an agent package document).");
        }

        if (package is null)
        {
            return Error("Agent package deserialized to null.");
        }

        WorkflowPackageImportPreview preview;
        try
        {
            preview = await importer.PreviewAsync(package, cancellationToken);
        }
        catch (WorkflowPackageResolutionException ex)
        {
            var payload = new
            {
                status = "invalid",
                message = ex.Message,
                missingReferences = ex.MissingReferences
                    .Select(r => new
                    {
                        kind = r.Kind.ToString(),
                        key = r.Key,
                        version = r.Version,
                        referencedBy = r.ReferencedBy,
                    })
                    .ToArray(),
                hint = "The package failed structural validation (schema version, entry-point " +
                       "presence in agents collection). Fix the structural issue and re-emit. " +
                       "Note: refs to roles / skills / MCP servers that already exist in the " +
                       "target library do NOT need to be embedded — only embed entities you " +
                       "are creating or intentionally bumping.",
            };
            return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
        }

        var note = AssistantToolJson.ReadOptionalString(arguments, "note");

        // Snapshot binding for the draft path: copy the validated draft bytes to an immutable
        // per-save snapshot file. Only snapshot when preview.CanApply (the chip will only
        // render in that case).
        Guid? draftSnapshotId = null;
        if (packageSource == "draft" && preview.CanApply && draftNodeForSnapshot is not null && workspace is not null)
        {
            try
            {
                draftSnapshotId = await AgentPackageDraftStore.WriteSnapshotAsync(
                    workspace, draftNodeForSnapshot, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error($"Could not snapshot the draft for confirmation: {ex.Message}");
            }

            if (artifactRecorder is not null)
            {
                var summaryNode = AgentPackageDraftStore.Summarize(
                    draftNodeForSnapshot,
                    new FileInfo(AgentPackageDraftStore.ResolveSnapshotPath(workspace, draftSnapshotId.Value)).Length);
                var snapshotName = Path.GetFileName(AgentPackageDraftStore.ResolveSnapshotPath(workspace, draftSnapshotId.Value));
                await artifactRecorder.RecordAsync(
                    conversationId: workspace.CorrelationId,
                    kind: ArtifactEventKind.AgentPackageSnapshot,
                    name: snapshotName,
                    relativePath: snapshotName,
                    snapshotId: draftSnapshotId,
                    summaryJson: summaryNode.ToJsonString(AssistantToolJson.SerializerOptions),
                    supersedesPriorByName: false,
                    cancellationToken: cancellationToken);
            }
        }

        var summary = new
        {
            status = preview.CanApply ? "preview_ok" : "preview_conflicts",
            entryPoint = new
            {
                key = preview.EntryPoint.Key,
                version = preview.EntryPoint.Version,
            },
            createCount = preview.CreateCount,
            reuseCount = preview.ReuseCount,
            conflictCount = preview.ConflictCount,
            refusedCount = preview.RefusedCount,
            warningCount = preview.WarningCount,
            warnings = preview.Warnings.ToArray(),
            items = preview.Items
                .Select(i => new
                {
                    kind = i.Kind.ToString(),
                    key = i.Key,
                    version = i.Version,
                    action = i.Action.ToString(),
                    message = i.Message,
                })
                .ToArray(),
            canApply = preview.CanApply,
            packageSource,
            snapshotId = draftSnapshotId?.ToString(),
            note,
            message = preview.CanApply
                ? "Preview validated. The user must click the 'Save' chip in chat to confirm — do not call this tool again or take further action until the user responds."
                : "Preview produced conflicts. A 'Resolve in imports page' chip will appear in chat that lets the user pick a resolution per row (Bump / UseExisting / Copy). Default to surfacing the conflicts and waiting for the user — do not re-emit the package on your own initiative. If the user explicitly asks you to resolve a specific conflict (e.g. by editing an entity to match the library), patch the draft with patch_agent_package_draft and call save_agent_package again.",
        };

        return new AssistantToolResult(JsonSerializer.Serialize(summary, AssistantToolJson.SerializerOptions));
    }

    private static AssistantToolResult Error(string message)
    {
        var payload = new { error = message };
        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions), IsError: true);
    }
}
