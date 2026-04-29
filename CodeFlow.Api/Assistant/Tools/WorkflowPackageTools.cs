using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;

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
public sealed class SaveWorkflowPackageTool(IWorkflowPackageImporter importer) : IAssistantTool
{
    public string Name => "save_workflow_package";

    public string Description =>
        "Validate a drafted workflow package against the library and offer to save it. The tool " +
        "runs a self-containment preview only; it does NOT save. The chat UI surfaces a 'Save' " +
        "confirmation chip — only the user clicking that chip persists the package. " +
        "Required: `package` (a complete codeflow.workflow-package.v1 document with every " +
        "referenced agent, role, skill, MCP server, and subflow embedded at its existing " +
        "version). Enum fields (workflow category, node kind, input kind, agent kind, MCP " +
        "transport, etc.) accept the canonical PascalCase string names you see in `get_workflow` " +
        "and `get_workflow_package` output (e.g. \"Workflow\", \"Agent\", \"Text\"). When in doubt " +
        "about the shape, call `get_workflow_package` on any existing workflow first and mirror " +
        "its layout. After invoking this tool, do NOT call it again or take further action; wait " +
        "for the user's next message.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""package"": {
                ""type"": ""object"",
                ""description"": ""A complete codeflow.workflow-package.v1 document. Must include every referenced agent / subflow / role / skill / MCP server at its existing version — the importer does not resolve from the database.""
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
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("package", out var packageElement)
            || packageElement.ValueKind != JsonValueKind.Object)
        {
            return Error("Argument `package` is required and must be an object (a workflow package document).");
        }

        WorkflowPackage? package;
        try
        {
            package = packageElement.Deserialize<WorkflowPackage>(AssistantToolJson.SerializerOptions);
        }
        catch (JsonException ex)
        {
            return Error($"Could not parse `package` as a workflow package document: {ex.Message}");
        }

        if (package is null)
        {
            return Error("Argument `package` deserialized to null.");
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
                hint = "Workflow packages must include every referenced entity at its existing " +
                       "version. Look up missing entities (e.g. via get_agent / get_workflow) and " +
                       "embed them in the corresponding package array, then re-emit the full package.",
            };
            return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
        }

        var note = AssistantToolJson.ReadOptionalString(arguments, "note");

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
