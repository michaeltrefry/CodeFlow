using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// CE-3: redact giant <c>tool_use.input</c> JSON for workflow-package tools after a successful
/// dispatch. The model emits the full workflow package (50–200 KB) as the <c>package</c> argument
/// to <c>set_workflow_package_draft</c> or the inline path of <c>save_workflow_package</c>. The
/// tool writes that payload to disk and returns a small summary, but the original argument lives
/// in the in-turn assistant transcript and gets resent verbatim on every subsequent loop
/// iteration. Replacing the <c>package</c> field with a small anchor (sha256 + size + structural
/// summary) strips the dead weight while keeping enough semantic content that the model's later
/// in-turn reasoning ("I just saved a draft with N nodes") still has something to anchor on.
/// </summary>
/// <remarks>
/// The streamed UI events (<c>AssistantToolCallStarted</c>) still carry the original arguments —
/// only the in-memory transcript fed to the next provider call is redacted, so the UI tool-call
/// cards display the package the user actually authored.
/// </remarks>
internal static class WorkflowPackageRedaction
{
    private static readonly HashSet<string> RedactableTools = new(StringComparer.Ordinal)
    {
        "set_workflow_package_draft",
        "save_workflow_package",
    };

    /// <summary>
    /// True if the tool's <c>tool_use.input</c> is a candidate for redaction (the inline
    /// workflow-package path of one of the assistant's package-handling tools). The dispatch
    /// outcome still has to be successful for the redaction to actually apply — see
    /// <see cref="RedactArgs"/>.
    /// </summary>
    public static bool IsRedactableTool(string toolName) => RedactableTools.Contains(toolName);

    /// <summary>
    /// Returns a redacted copy of <paramref name="originalArgs"/> if (a) the tool is in the
    /// redactable set and (b) the <c>package</c> argument is a JSON object. Otherwise returns
    /// <paramref name="originalArgs"/> unchanged.
    ///
    /// <para>The redacted shape is:</para>
    /// <code>
    /// {
    ///   "package": {
    ///     "_redacted": true,
    ///     "sha256": "&lt;hex&gt;",
    ///     "sizeBytes": &lt;n&gt;,
    ///     "summary": { "workflowCount": N, "nodeCount": N, "agentCount": N, "entryPoint": &lt;original&gt; }
    ///   }
    /// }
    /// </code>
    ///
    /// Other top-level args (if any are added later) are preserved as-is.
    /// </summary>
    public static JsonElement RedactArgs(string toolName, JsonElement originalArgs)
    {
        if (!IsRedactableTool(toolName)) return originalArgs;
        if (originalArgs.ValueKind != JsonValueKind.Object) return originalArgs;
        if (!originalArgs.TryGetProperty("package", out var pkg)) return originalArgs;
        if (pkg.ValueKind != JsonValueKind.Object) return originalArgs;

        var packageJson = pkg.GetRawText();
        var packageBytes = Encoding.UTF8.GetBytes(packageJson);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(packageBytes));

        var summary = new JsonObject();
        if (pkg.TryGetProperty("workflows", out var workflows) && workflows.ValueKind == JsonValueKind.Array)
        {
            var nodeCount = 0;
            foreach (var wf in workflows.EnumerateArray())
            {
                if (wf.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                {
                    nodeCount += nodes.GetArrayLength();
                }
            }
            summary["workflowCount"] = workflows.GetArrayLength();
            summary["nodeCount"] = nodeCount;
        }
        if (pkg.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array)
        {
            summary["agentCount"] = agents.GetArrayLength();
        }
        if (pkg.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
        {
            summary["roleCount"] = roles.GetArrayLength();
        }
        if (pkg.TryGetProperty("entryPoint", out var entryPoint))
        {
            summary["entryPoint"] = JsonNode.Parse(entryPoint.GetRawText());
        }

        var redactedPackage = new JsonObject
        {
            ["_redacted"] = true,
            ["sha256"] = sha256,
            ["sizeBytes"] = packageBytes.Length,
            ["summary"] = summary,
        };

        var output = new JsonObject();
        foreach (var prop in originalArgs.EnumerateObject())
        {
            output[prop.Name] = prop.NameEquals("package")
                ? redactedPackage
                : JsonNode.Parse(prop.Value.GetRawText());
        }

        return JsonSerializer.SerializeToElement(output);
    }

    /// <summary>
    /// Anthropic-flavoured projection: returns the redacted args as a
    /// <c>Dictionary&lt;string, JsonElement&gt;</c> ready to assign to
    /// <see cref="Anthropic.Models.Messages.ToolUseBlockParam.Input"/>.
    /// Falls back to a verbatim conversion of <paramref name="originalArgs"/> when no redaction
    /// applies.
    /// </summary>
    public static Dictionary<string, JsonElement> RedactArgsAsDictionary(string toolName, JsonElement originalArgs)
    {
        var redacted = RedactArgs(toolName, originalArgs);
        return ToDictionary(redacted);
    }

    /// <summary>
    /// OpenAI-flavoured projection: returns the redacted args as a JSON string ready to wrap in
    /// the <see cref="System.BinaryData"/> that <see cref="OpenAI.Chat.ChatToolCall"/> expects.
    /// </summary>
    public static string RedactArgsAsJsonString(string toolName, JsonElement originalArgs)
    {
        var redacted = RedactArgs(toolName, originalArgs);
        return redacted.GetRawText();
    }

    private static Dictionary<string, JsonElement> ToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.Clone();
        }
        return dict;
    }
}
