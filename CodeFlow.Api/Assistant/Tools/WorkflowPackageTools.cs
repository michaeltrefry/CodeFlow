using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using CodeFlow.Runtime;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// HAA-10: lets the assistant offer to save a drafted workflow package into the library, gated by
/// a UI-side confirmation chip.
/// </summary>
/// <remarks>
/// The tool itself does NOT mutate. It runs <see cref="IWorkflowPackageImporter.PreviewAsync"/>
/// to validate self-containment (catching <see cref="WorkflowPackageResolutionException"/>) and
/// returns the preview verdict. The chat UI detects the tool by name, surfaces a confirmation
/// chip carrying the original package payload from the SSE <c>tool-call</c> event, and on confirm
/// posts directly to <c>POST /api/workflows/package/apply</c> (which already requires
/// <c>WorkflowsWrite</c> and runs as the logged-in user).
///
/// This split keeps the assistant's tool loop synchronous — there is no need to pause mid-stream
/// for human interaction — while still ensuring (a) every save is gated by an explicit human
/// click and (b) the import path, audit, validation, and permission checks are the same as a
/// regular library import.
/// </remarks>
public sealed class SaveWorkflowPackageTool : IAssistantTool
{
    private readonly IWorkflowPackageImporter importer;
    private readonly ToolWorkspaceContext? workspace;

    /// <summary>
    /// Constructor used by the per-turn factory. <paramref name="workspace"/> is non-null when
    /// the conversation has a writable workspace — in that mode the tool also accepts a zero-arg
    /// invocation and reads the package from <c>draft.cf-workflow-package.json</c> in the
    /// workspace, so the LLM doesn't have to re-emit the full payload.
    /// </summary>
    public SaveWorkflowPackageTool(IWorkflowPackageImporter importer, ToolWorkspaceContext? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(importer);
        this.importer = importer;
        this.workspace = workspace;
    }

    public string Name => "save_workflow_package";

    public string Description =>
        "Validate a drafted workflow package against the library and offer to save it. The tool " +
        "runs a preview AND the same validation the import endpoint runs at apply time; it does " +
        "NOT save. The chat UI surfaces a 'Save' confirmation chip — only the user clicking that " +
        "chip persists the package. " +
        (workspace is not null
            ? "There are TWO ways to invoke this tool: (1) pass a `package` object to validate it " +
              "directly, OR (2) call with no arguments to validate the conversation's draft package " +
              "saved via `set_workflow_package_draft`. Form (2) is strongly preferred for refinement " +
              "loops because the LLM does not have to re-emit the full payload on every save attempt. "
            : "Required: `package` (a codeflow.workflow-package.v1 document). ") +
        "You do NOT need to embed unchanged dependencies that already exist in the target library " +
        "— the importer resolves any (key, version) referenced by a node but omitted from the " +
        "package against the local DB and reports it as a Reuse in the preview. Only embed an " +
        "entity when you're creating it or intentionally bumping it. Enum fields accept the " +
        "canonical PascalCase strings you see in `get_workflow` / `get_workflow_package` output. " +
        "After invoking this tool, do NOT call it again or take further action; wait for the " +
        "user's next message.";

    public JsonElement InputSchema => AssistantToolJson.Schema(workspace is not null
        ? @"{
        ""type"": ""object"",
        ""properties"": {
            ""package"": {
                ""type"": ""object"",
                ""description"": ""Optional. A codeflow.workflow-package.v1 document. Omit to use the conversation's draft (saved via set_workflow_package_draft) — preferred for refinement loops to avoid re-emitting the payload.""
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
                ""description"": ""A codeflow.workflow-package.v1 document carrying the new or changed entities.""
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
        WorkflowPackage? package;
        var packageSource = "inline";
        // Holds the parsed JSON for the draft path so we can snapshot it after validation
        // succeeds without re-serializing the typed `package` (which would re-order fields and
        // break byte-for-byte equality with what the LLM saw).
        JsonNode? draftNodeForSnapshot = null;

        // Distinguish three argument shapes carefully:
        //   - `package` absent entirely → draft path (or error if no workspace)
        //   - `package` present and an object → inline path
        //   - `package` present but null / string / array / number → hard error (do NOT silently
        //     fall through to the draft path; that would let a malformed inline save apply a
        //     stale draft from disk while the user thinks they're saving the value they sent)
        JsonElement packageElement = default;
        var packagePropertyPresent = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("package", out packageElement);

        if (packagePropertyPresent && packageElement.ValueKind == JsonValueKind.Object)
        {
            if (WorkflowPackageRedaction.IsRedactionPlaceholder(packageElement))
            {
                return Error(
                    "The `package` argument is a redaction placeholder, not a real workflow package. "
                    + "The redacted shape `{\"_redacted\": true, \"sha256\": ..., \"summary\": ...}` "
                    + "is what your prior tool_use Inputs are replaced with in your transcript history "
                    + "to save tokens — it is NOT a callable input. Either omit the `package` argument "
                    + "to use the conversation's draft (preferred), or re-emit the actual workflow-package JSON.");
            }

            try
            {
                package = packageElement.Deserialize<WorkflowPackage>(AssistantToolJson.SerializerOptions);
            }
            catch (JsonException ex)
            {
                return Error($"Could not parse `package` as a workflow package document: {ex.Message}");
            }
        }
        else if (packagePropertyPresent)
        {
            return Error(
                $"Argument `package` is present but its JSON kind is `{packageElement.ValueKind}`. "
                + "It must be a workflow package object, or omitted entirely to use the conversation's draft.");
        }
        else if (workspace is not null)
        {
            // Zero-arg form: read the conversation's draft from disk.
            try
            {
                draftNodeForSnapshot = await WorkflowPackageDraftStore.ReadAsync(workspace, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error($"Failed to read draft package from workspace: {ex.Message}");
            }

            if (draftNodeForSnapshot is null)
            {
                return Error("No draft package has been saved for this conversation. Call `set_workflow_package_draft` first, or pass a `package` argument directly.");
            }

            try
            {
                package = draftNodeForSnapshot.Deserialize<WorkflowPackage>(AssistantToolJson.SerializerOptions);
            }
            catch (JsonException ex)
            {
                return Error($"Could not parse the conversation's draft as a workflow package document: {ex.Message}");
            }
            packageSource = "draft";
        }
        else
        {
            return Error("Argument `package` is required and must be an object (a workflow package document).");
        }

        if (package is null)
        {
            return Error("Workflow package deserialized to null.");
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
                       "presence, or a node carrying an agent/subflow ref without a concrete " +
                       "version pin). Fix the structural issue and re-emit. Note: refs to " +
                       "entities that already exist in the target library do NOT need to be " +
                       "embedded — only embed entities you are creating or intentionally bumping.",
            };
            return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
        }

        // Run the same validators the apply endpoint runs, in a rollback-only transaction.
        // Without this, the assistant would tell the user "preview validated, click Save" and
        // then the click would fail at apply time on rules the apply endpoint enforces (Start
        // node present, every node reachable, declared port hygiene, etc.).
        if (preview.CanApply)
        {
            WorkflowPackageValidationResult validation;
            try
            {
                validation = await importer.ValidateAsync(package, cancellationToken);
            }
            catch (WorkflowPackageResolutionException ex)
            {
                var payload = new
                {
                    status = "invalid",
                    message = ex.Message,
                    hint = "The package was rejected during validation. Fix the issue and re-emit.",
                };
                return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
            }

            if (!validation.IsValid)
            {
                var payload = new
                {
                    status = "invalid",
                    message = "The package would be rejected at save time. Fix the listed errors and re-emit.",
                    errors = validation.Errors
                        .Select(e => new
                        {
                            workflowKey = e.WorkflowKey,
                            message = e.Message,
                            ruleIds = e.RuleIds ?? Array.Empty<string>(),
                        })
                        .ToArray(),
                    hint = "These are the same rules the visual editor and the import endpoint " +
                           "enforce. Common offenders: missing Start node, a node not reachable " +
                           "from Start, maxRoundsPerRound out of [1..50], or an edge using a port " +
                           "the source node doesn't declare.",
                };
                return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
            }
        }

        var note = AssistantToolJson.ReadOptionalString(arguments, "note");

        // Snapshot binding for the draft path: copy the validated draft bytes to an immutable
        // per-save snapshot file, return its id, and have the chat UI carry the id through to
        // the apply endpoint. Without this the user could approve one package while the live
        // draft.cf-workflow-package.json file gets patched/overwritten before they click Save,
        // and the apply endpoint would import the *current* draft, not the one they confirmed.
        // Only snapshot when preview.CanApply (the chip will only render in that case).
        Guid? draftSnapshotId = null;
        if (packageSource == "draft" && preview.CanApply && draftNodeForSnapshot is not null && workspace is not null)
        {
            try
            {
                draftSnapshotId = await WorkflowPackageDraftStore.WriteSnapshotAsync(
                    workspace, draftNodeForSnapshot, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error($"Could not snapshot the draft for confirmation: {ex.Message}");
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
            // Tells the chat UI which apply path to use on the Save chip:
            //   "inline" → POST /api/workflows/package/apply with the cached package payload
            //   "draft"  → POST /api/workflows/package/apply-from-draft with conversationId + snapshotId
            // The "draft" path keeps the full payload server-side so refinement loops don't
            // round-trip the package through the LLM or the browser; the snapshot id binds the
            // chip to the exact bytes that were validated, immune to subsequent draft mutations.
            packageSource,
            // null on the inline path; populated GUID on the draft path when preview.CanApply.
            // Emit in default "D" (hyphenated) format. The chat panel passes this string straight
            // through to the apply-from-draft body, where it binds to a `Guid` field — and
            // System.Text.Json's default Guid converter ONLY accepts D-format. An "N"-format id
            // (32 hex, no dashes) fails model binding before the handler runs and produces a
            // 400 with an empty body that the chip's error formatter can't surface.
            snapshotId = draftSnapshotId?.ToString(),
            note,
            message = preview.CanApply
                ? "Preview validated. The user must click the 'Save' chip in chat to confirm — do not call this tool again or take further action until the user responds."
                : "Preview produced conflicts. The package cannot be applied as-is. Tell the user which conflicts to resolve and re-emit the package.",
        };

        return new AssistantToolResult(JsonSerializer.Serialize(summary, AssistantToolJson.SerializerOptions));
    }

    private static AssistantToolResult Error(string message)
    {
        var payload = new { error = message };
        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions), IsError: true);
    }
}

/// <summary>
/// Read-only companion to <see cref="SaveWorkflowPackageTool"/>: returns the canonical
/// codeflow.workflow-package.v1 document for an existing workflow so the LLM has a concrete shape
/// exemplar to mirror when drafting a new package. Wraps <see cref="IWorkflowPackageResolver"/> —
/// the same builder the UI's "Export package" button uses — so the output matches the import
/// path's expected schema field-for-field.
/// </summary>
/// <remarks>
/// The result must fit inside the dispatcher's 32 KB budget, so the tool truncates large
/// free-form fields (agent system prompts, prompt templates, node input/output scripts, node
/// templates, skill bodies, MCP tool parameter schemas) at 4 KB with a clear marker. The model
/// can call <c>get_agent</c>, <c>get_workflow</c>, etc. for full bodies if it needs them — the
/// purpose here is structural learning, not byte-for-byte cloning.
/// </remarks>
public sealed class GetWorkflowPackageTool(
    IWorkflowPackageResolver resolver,
    IWorkflowRepository workflowRepository) : IAssistantTool
{
    private const int LongFieldCap = 4096;

    public string Name => "get_workflow_package";

    public string Description =>
        "Get the canonical codeflow.workflow-package.v1 document for an existing workflow. " +
        "Returns the same shape `save_workflow_package` accepts — call this first when drafting " +
        "a new package so you can mirror the field names, enum casing, and nesting exactly. " +
        "Long free-form fields (system prompts, prompt templates, node scripts, skill bodies) " +
        "are truncated to 4 KB with a marker; fetch full bodies via `get_agent` or `get_workflow` " +
        "if you need them. If `version` is omitted, returns the latest version.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""key"": { ""type"": ""string"", ""description"": ""Workflow key (required)."" },
            ""version"": { ""type"": ""integer"", ""description"": ""Specific version. Omit to get the latest."" }
        },
        ""required"": [""key""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var key = AssistantToolJson.ReadOptionalString(arguments, "key");
        if (string.IsNullOrWhiteSpace(key))
        {
            return Error("Argument `key` is required.");
        }

        var requestedVersion = AssistantToolJson.ReadOptionalInt(arguments, "version");

        int resolvedVersion;
        if (requestedVersion is int explicitVersion)
        {
            resolvedVersion = explicitVersion;
        }
        else
        {
            var latest = await workflowRepository.GetLatestAsync(key, cancellationToken);
            if (latest is null)
            {
                return Error($"Workflow '{key}' not found.");
            }
            resolvedVersion = latest.Version;
        }

        WorkflowPackage package;
        try
        {
            package = await resolver.ResolveAsync(key, resolvedVersion, cancellationToken);
        }
        catch (WorkflowNotFoundException)
        {
            return Error($"Workflow '{key}' v{resolvedVersion} not found.");
        }
        catch (WorkflowPackageResolutionException ex)
        {
            // Self-containment failure during *resolution* means the stored workflow references
            // an entity that no longer exists in the library. Surface it instead of pretending the
            // package is fetchable — the LLM (and downstream user) need to know.
            var payload = new
            {
                error = "Could not build a self-contained package from this workflow.",
                detail = ex.Message,
                missingReferences = ex.MissingReferences
                    .Select(r => new
                    {
                        kind = r.Kind.ToString(),
                        key = r.Key,
                        version = r.Version,
                        referencedBy = r.ReferencedBy,
                    })
                    .ToArray(),
            };
            return new AssistantToolResult(
                JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions),
                IsError: true);
        }

        var trimmed = TrimForLlm(package);
        var json = JsonSerializer.Serialize(trimmed, AssistantToolJson.SerializerOptions);
        return new AssistantToolResult(json);
    }

    private static WorkflowPackage TrimForLlm(WorkflowPackage source) => source with
    {
        Workflows = source.Workflows
            .Select(workflow => workflow with
            {
                Nodes = workflow.Nodes
                    .Select(node => node with
                    {
                        InputScript = AssistantToolJson.TruncateText(node.InputScript, LongFieldCap),
                        OutputScript = AssistantToolJson.TruncateText(node.OutputScript, LongFieldCap),
                        Template = AssistantToolJson.TruncateText(node.Template, LongFieldCap),
                    })
                    .ToArray(),
            })
            .ToArray(),
        Agents = source.Agents
            .Select(agent => agent with { Config = TrimJsonNode(agent.Config) })
            .ToArray(),
        Skills = source.Skills
            .Select(skill => skill with { Body = AssistantToolJson.TruncateText(skill.Body, LongFieldCap) })
            .ToArray(),
        McpServers = source.McpServers
            .Select(server => server with
            {
                Tools = server.Tools
                    .Select(tool => tool with { Parameters = TrimJsonNode(tool.Parameters) })
                    .ToArray(),
            })
            .ToArray(),
    };

    private static JsonNode? TrimJsonNode(JsonNode? node)
    {
        if (node is null) return null;
        var serialized = node.ToJsonString();
        if (serialized.Length <= LongFieldCap) return node;
        // Replace the oversized blob with a JSON-string placeholder. The shape is no longer the
        // original (object becomes string), but the field is preserved so the model still sees
        // it exists — and the marker tells the model to fetch the full body via get_agent.
        return JsonValue.Create(AssistantToolJson.TruncateText(serialized, LongFieldCap));
    }

    private static AssistantToolResult Error(string message)
    {
        var payload = new { error = message };
        return new AssistantToolResult(
            JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions),
            IsError: true);
    }
}
