using System.Text.Json;
using CodeFlow.Persistence;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// HAA-11: lets the assistant offer to start a workflow run, gated by a UI-side confirmation chip.
/// </summary>
/// <remarks>
/// Mirrors HAA-10's split: the LLM-callable tool is read-only validation; the actual mutation is
/// a direct UI POST to the existing <c>POST /api/traces</c> endpoint after the user clicks the
/// chip. The tool resolves the workflow (latest version when unspecified), checks the supplied
/// inputs against the workflow's declared <see cref="WorkflowInput"/> schema, and returns a
/// preview verdict the chat UI uses to render the chip.
///
/// This split keeps the assistant's tool loop synchronous — there is no need to pause mid-stream
/// for a UI click — while still ensuring (a) every run is gated by an explicit human click,
/// (b) the trace creation path, audit, validation, and permission checks are the same as a
/// regular run from the workflows page, and (c) the run executes under the logged-in user's
/// identity (the apply endpoint requires <c>TracesWrite</c>).
/// </remarks>
public sealed class RunWorkflowTool(IWorkflowRepository repository) : IAssistantTool
{
    public string Name => "run_workflow";

    public string Description =>
        "Validate a workflow run request and offer to start it. The tool runs schema validation " +
        "only; it does NOT trigger the run. The chat UI surfaces a 'Run' confirmation chip — " +
        "only the user clicking that chip starts the trace. " +
        "Required: `workflowKey` and `input` (the workflow's primary input string consumed by the " +
        "start node). Optional: `workflowVersion` (defaults to latest), `inputFileName`, and " +
        "`inputs` (object whose keys must match the workflow's declared input keys). " +
        "If the result is `inputs_missing`, ask the user for the missing inputs in chat and " +
        "re-invoke this tool with them — do NOT call the tool again until you have the inputs. " +
        "After a successful preview, do NOT call this tool again or take further action; wait " +
        "for the user's next message.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""workflowKey"": {
                ""type"": ""string"",
                ""description"": ""The workflow's stable key (slug). Use list_workflows / find_workflows_using_agent if unknown.""
            },
            ""workflowVersion"": {
                ""type"": ""integer"",
                ""description"": ""Pin to a specific version. Defaults to the latest version when omitted.""
            },
            ""input"": {
                ""type"": ""string"",
                ""description"": ""The primary input string fed to the workflow's start node. The workflow's start agent consumes this directly.""
            },
            ""inputFileName"": {
                ""type"": ""string"",
                ""description"": ""Optional human-readable file name for the input artifact (defaults to 'input.txt').""
            },
            ""inputs"": {
                ""type"": ""object"",
                ""description"": ""Map of input key → value. Keys must match the workflow's declared inputs. Required inputs without defaults must be supplied here. Values for Text inputs should be strings; values for Json inputs should be the parsed JSON shape.""
            }
        },
        ""required"": [""workflowKey"", ""input""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var workflowKey = AssistantToolJson.ReadOptionalString(arguments, "workflowKey");
        if (string.IsNullOrWhiteSpace(workflowKey))
        {
            return Error("Argument `workflowKey` is required.");
        }

        var primaryInput = ReadInputString(arguments);
        if (string.IsNullOrWhiteSpace(primaryInput))
        {
            return Error("Argument `input` is required (the workflow's primary input string).");
        }

        var workflowVersion = AssistantToolJson.ReadOptionalInt(arguments, "workflowVersion");
        var inputFileName = AssistantToolJson.ReadOptionalString(arguments, "inputFileName");

        Workflow workflow;
        try
        {
            if (workflowVersion is int version)
            {
                workflow = await repository.GetAsync(workflowKey, version, cancellationToken);
            }
            else
            {
                var latest = await repository.GetLatestAsync(workflowKey, cancellationToken);
                if (latest is null)
                {
                    return new AssistantToolResult(JsonSerializer.Serialize(new
                    {
                        status = "not_found",
                        message = $"Workflow '{workflowKey}' was not found in the library.",
                    }, AssistantToolJson.SerializerOptions));
                }
                workflow = latest;
            }
        }
        catch (WorkflowNotFoundException)
        {
            return new AssistantToolResult(JsonSerializer.Serialize(new
            {
                status = "not_found",
                message = $"Workflow '{workflowKey}' v{workflowVersion} was not found.",
            }, AssistantToolJson.SerializerOptions));
        }

        var supplied = ReadInputsObject(arguments);
        var validation = ValidateInputs(workflow, supplied);

        if (validation.Status != "preview_ok")
        {
            return new AssistantToolResult(JsonSerializer.Serialize(new
            {
                status = validation.Status,
                workflow = WorkflowSummary(workflow),
                missingInputs = validation.MissingInputs,
                unknownInputs = validation.UnknownInputs,
                errors = validation.Errors,
                message = validation.Message,
            }, AssistantToolJson.SerializerOptions));
        }

        var summary = new
        {
            status = "preview_ok",
            workflow = WorkflowSummary(workflow),
            input = AssistantToolJson.TruncateText(primaryInput, 4096),
            inputFileName = inputFileName ?? "input.txt",
            resolvedInputs = validation.ResolvedInputs,
            declaredInputs = workflow.Inputs
                .OrderBy(i => i.Ordinal)
                .Select(i => new
                {
                    key = i.Key,
                    displayName = i.DisplayName,
                    kind = i.Kind.ToString(),
                    required = i.Required,
                    hasDefault = !string.IsNullOrEmpty(i.DefaultValueJson),
                    description = i.Description,
                })
                .ToArray(),
            message = "Preview validated. The user must click the 'Run' chip in chat to start " +
                      "the trace — do not call this tool again or take further action until the " +
                      "user responds.",
        };

        return new AssistantToolResult(JsonSerializer.Serialize(summary, AssistantToolJson.SerializerOptions));
    }

    private static object WorkflowSummary(Workflow workflow) => new
    {
        key = workflow.Key,
        version = workflow.Version,
        name = workflow.Name,
        category = workflow.Category.ToString(),
    };

    private static string? ReadInputString(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("input", out var prop))
        {
            return null;
        }
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static IReadOnlyDictionary<string, JsonElement>? ReadInputsObject(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("inputs", out var prop))
        {
            return null;
        }
        if (prop.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in prop.EnumerateObject())
        {
            dict[p.Name] = p.Value.Clone();
        }
        return dict;
    }

    private static InputValidation ValidateInputs(Workflow workflow, IReadOnlyDictionary<string, JsonElement>? supplied)
    {
        var resolved = new Dictionary<string, object?>(StringComparer.Ordinal);
        var missing = new List<string>();
        var unknown = new List<string>();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        var declaredKeys = new HashSet<string>(
            workflow.Inputs.Select(i => i.Key),
            StringComparer.Ordinal);

        if (supplied is not null)
        {
            foreach (var key in supplied.Keys)
            {
                if (!declaredKeys.Contains(key))
                {
                    unknown.Add(key);
                }
            }
        }

        foreach (var def in workflow.Inputs.OrderBy(i => i.Ordinal))
        {
            if (supplied is not null && supplied.TryGetValue(def.Key, out var value))
            {
                if (def.Kind == WorkflowInputKind.Text && value.ValueKind != JsonValueKind.String)
                {
                    errors[def.Key] = $"Input '{def.Key}' (Text) must be a string; received {value.ValueKind}.";
                    continue;
                }
                resolved[def.Key] = JsonSerializer.Deserialize<object?>(value.GetRawText());
                continue;
            }

            if (!string.IsNullOrEmpty(def.DefaultValueJson))
            {
                using var doc = JsonDocument.Parse(def.DefaultValueJson);
                resolved[def.Key] = JsonSerializer.Deserialize<object?>(doc.RootElement.GetRawText());
                continue;
            }

            if (def.Required)
            {
                missing.Add(def.Key);
            }
        }

        if (missing.Count > 0 && unknown.Count == 0 && errors.Count == 0)
        {
            return new InputValidation(
                Status: "inputs_missing",
                ResolvedInputs: resolved,
                MissingInputs: missing.ToArray(),
                UnknownInputs: Array.Empty<string>(),
                Errors: null,
                Message: "Some required inputs are missing. Ask the user for these and re-invoke " +
                         "the tool with them via the `inputs` object.");
        }

        if (unknown.Count > 0 || errors.Count > 0 || missing.Count > 0)
        {
            return new InputValidation(
                Status: "invalid",
                ResolvedInputs: resolved,
                MissingInputs: missing.ToArray(),
                UnknownInputs: unknown.ToArray(),
                Errors: errors.Count == 0 ? null : errors,
                Message: BuildInvalidMessage(missing, unknown, errors));
        }

        return new InputValidation(
            Status: "preview_ok",
            ResolvedInputs: resolved,
            MissingInputs: Array.Empty<string>(),
            UnknownInputs: Array.Empty<string>(),
            Errors: null,
            Message: null);
    }

    private static string BuildInvalidMessage(
        IReadOnlyList<string> missing,
        IReadOnlyList<string> unknown,
        IReadOnlyDictionary<string, string> errors)
    {
        var parts = new List<string>();
        if (missing.Count > 0)
        {
            parts.Add($"missing required inputs: {string.Join(", ", missing)}");
        }
        if (unknown.Count > 0)
        {
            parts.Add($"unknown input keys: {string.Join(", ", unknown)}");
        }
        if (errors.Count > 0)
        {
            parts.Add($"type errors: {string.Join("; ", errors.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");
        }
        return $"Cannot start the workflow because the inputs are invalid — {string.Join(" / ", parts)}.";
    }

    private static AssistantToolResult Error(string message)
    {
        var payload = new { error = message };
        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions), IsError: true);
    }

    private sealed record InputValidation(
        string Status,
        IReadOnlyDictionary<string, object?> ResolvedInputs,
        IReadOnlyList<string> MissingInputs,
        IReadOnlyList<string> UnknownInputs,
        IReadOnlyDictionary<string, string>? Errors,
        string? Message);
}
