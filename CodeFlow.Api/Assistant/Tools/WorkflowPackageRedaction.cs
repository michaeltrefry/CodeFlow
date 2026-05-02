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
    /// <summary>
    /// Stable marker the tools check on the inbound side to refuse redacted-shape arguments —
    /// see <see cref="IsRedactionPlaceholder"/>. The same string is rendered into the
    /// redaction object's <c>_doNotCopy</c> field so a model that reads it has explicit
    /// instructions even without the system prompt's clause.
    /// </summary>
    public const string DoNotCopyNotice =
        "This is a transcript stub for a payload you previously emitted, not a callable input. "
        + "Do not copy this object into a future tool_use. To inspect the current draft call "
        + "get_workflow_package_draft; to edit it call patch_workflow_package_draft; to re-emit "
        + "use the actual workflow-package JSON.";

    private static readonly HashSet<string> RedactableInputTools = new(StringComparer.Ordinal)
    {
        "set_workflow_package_draft",
        "save_workflow_package",
    };

    private static readonly HashSet<string> RedactableResultTools = new(StringComparer.Ordinal)
    {
        "get_workflow_package_draft",
        "get_workflow_package",
    };

    /// <summary>
    /// True if the tool's <c>tool_use.input</c> is a candidate for redaction (the inline
    /// workflow-package path of one of the assistant's package-handling tools). The dispatch
    /// outcome still has to be successful for the redaction to actually apply — see
    /// <see cref="RedactArgs"/>.
    /// </summary>
    public static bool IsRedactableTool(string toolName) => RedactableInputTools.Contains(toolName);

    /// <summary>
    /// True if the tool's <c>tool_result</c> body carries a full workflow package payload
    /// (<c>get_workflow_package_draft</c>, <c>get_workflow_package</c>). Symmetric to
    /// <see cref="IsRedactableTool"/> on the input direction; combined with the carrier
    /// projection these two predicates bound the in-transcript package payload count to one.
    /// </summary>
    public static bool IsRedactableResultTool(string toolName) => RedactableResultTools.Contains(toolName);

    /// <summary>
    /// True if <paramref name="packageElement"/> is the redaction placeholder shape (a JSON
    /// object whose <c>_redacted</c> property is the boolean <c>true</c>) rather than a real
    /// workflow package. The package-writing tools call this on the inbound side to refuse
    /// the placeholder — without this guard a model that copies its own redacted prior
    /// emission would write the stub to disk.
    /// </summary>
    public static bool IsRedactionPlaceholder(JsonElement packageElement)
    {
        if (packageElement.ValueKind != JsonValueKind.Object) return false;
        return packageElement.TryGetProperty("_redacted", out var marker)
            && marker.ValueKind == JsonValueKind.True;
    }

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
            ["_doNotCopy"] = DoNotCopyNotice,
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

    /// <summary>
    /// True when <paramref name="args"/> carries a redactable workflow-package object as its
    /// <c>package</c> field on a redactable input tool. Distinct from
    /// <see cref="IsRedactableTool"/>: a save_workflow_package call that omits <c>package</c>
    /// (the draft path) is a redactable tool but NOT a carrier.
    /// </summary>
    public static bool InputCarriesPackagePayload(string toolName, JsonElement args)
    {
        if (!IsRedactableTool(toolName)) return false;
        if (args.ValueKind != JsonValueKind.Object) return false;
        if (!args.TryGetProperty("package", out var pkg)) return false;
        if (pkg.ValueKind != JsonValueKind.Object) return false;
        return !IsRedactionPlaceholder(pkg);
    }

    /// <summary>
    /// True when <paramref name="resultJson"/> carries a real (non-redacted) workflow-package
    /// payload from a redactable result tool. A failed-result body or an already-redacted body
    /// returns false — the carrier tracker uses this to skip slots that have nothing to redact.
    /// </summary>
    public static bool ResultCarriesPackagePayload(string toolName, string resultJson)
    {
        if (!IsRedactableResultTool(toolName)) return false;
        if (string.IsNullOrEmpty(resultJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (toolName == "get_workflow_package_draft")
            {
                return root.TryGetProperty("package", out var pkg)
                    && pkg.ValueKind == JsonValueKind.Object
                    && !IsRedactionPlaceholder(pkg);
            }
            return !IsRedactionPlaceholder(root)
                && (root.TryGetProperty("schemaVersion", out _) || root.TryGetProperty("workflows", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns a redacted copy of a tool-result JSON string for one of the package-fetching
    /// tools. Two shapes are handled:
    /// <list type="bullet">
    /// <item><description><c>get_workflow_package_draft</c>: returns
    ///   <c>{ "status": "ok", "package": {...} }</c> — the <c>package</c> field is replaced.</description></item>
    /// <item><description><c>get_workflow_package</c>: the entire result body IS the package —
    ///   the whole top-level object is replaced with the redaction shape.</description></item>
    /// </list>
    /// Returns <paramref name="originalResultJson"/> unchanged if the tool is not a redactable
    /// result carrier, the JSON is not parseable, or the package payload is missing.
    /// </summary>
    public static string RedactResultJson(string toolName, string originalResultJson)
    {
        if (!IsRedactableResultTool(toolName)) return originalResultJson;
        if (string.IsNullOrEmpty(originalResultJson)) return originalResultJson;

        JsonNode? root;
        try { root = JsonNode.Parse(originalResultJson); }
        catch (JsonException) { return originalResultJson; }
        if (root is not JsonObject obj) return originalResultJson;

        if (toolName == "get_workflow_package_draft")
        {
            // Wrapper shape: replace only the package field. Preserve status/message/etc.
            if (obj["package"] is not JsonObject packageObj) return originalResultJson;
            obj["package"] = BuildRedactedPackageObject(packageObj);
            return obj.ToJsonString(); // serializer-options not needed; default round-trip is fine
        }

        // get_workflow_package: the whole body IS the package — replace it wholesale.
        return BuildRedactedPackageObject(obj).ToJsonString();
    }

    /// <summary>
    /// Returns true if a tool-result body is already redacted — i.e., its top-level object (or
    /// its <c>package</c> field) carries the <c>_redacted: true</c> marker. Used by the
    /// carrier tracker to skip double-redaction work.
    /// </summary>
    public static bool IsResultRedacted(string toolName, string resultJson)
    {
        if (!IsRedactableResultTool(toolName)) return false;
        if (string.IsNullOrEmpty(resultJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (toolName == "get_workflow_package_draft")
            {
                return root.TryGetProperty("package", out var pkg) && IsRedactionPlaceholder(pkg);
            }
            return IsRedactionPlaceholder(root);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Build the redacted-package-object for a real package <see cref="JsonObject"/>. Mirrors
    /// <see cref="RedactArgs"/>'s replacement value but operates on a <see cref="JsonNode"/>
    /// directly so it can be reused by the result-redaction path without round-tripping
    /// through <see cref="JsonElement"/>.
    /// </summary>
    private static JsonObject BuildRedactedPackageObject(JsonObject pkg)
    {
        var packageJson = pkg.ToJsonString();
        var packageBytes = Encoding.UTF8.GetBytes(packageJson);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(packageBytes));

        var summary = new JsonObject();
        if (pkg["workflows"] is JsonArray workflows)
        {
            var nodeCount = 0;
            foreach (var wf in workflows)
            {
                if (wf is JsonObject wfObj && wfObj["nodes"] is JsonArray nodes)
                {
                    nodeCount += nodes.Count;
                }
            }
            summary["workflowCount"] = workflows.Count;
            summary["nodeCount"] = nodeCount;
        }
        if (pkg["agents"] is JsonArray agents) summary["agentCount"] = agents.Count;
        if (pkg["roles"] is JsonArray roles) summary["roleCount"] = roles.Count;
        if (pkg["entryPoint"] is JsonObject entryPoint)
        {
            summary["entryPoint"] = JsonNode.Parse(entryPoint.ToJsonString());
        }

        return new JsonObject
        {
            ["_redacted"] = true,
            ["_doNotCopy"] = DoNotCopyNotice,
            ["sha256"] = sha256,
            ["sizeBytes"] = packageBytes.Length,
            ["summary"] = summary,
        };
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
