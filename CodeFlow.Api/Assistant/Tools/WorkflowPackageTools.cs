using System.Text.Json;
using CodeFlow.Api.WorkflowPackages;

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
        "version). After invoking this tool, do NOT call it again or take further action; wait " +
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
