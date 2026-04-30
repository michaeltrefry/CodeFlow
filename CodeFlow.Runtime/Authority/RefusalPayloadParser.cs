using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Authority;

/// <summary>
/// Parses the structured refusal payload that workspace tools (sc-270) and future authority
/// boundaries emit when returning <see cref="ToolResult.IsError"/> = true. The payload shape:
///
/// <code>
/// {
///   "ok": false,
///   "refusal": {
///     "code": "preimage-mismatch",
///     "reason": "...",
///     "axis": "workspace-mutation",
///     "path": "src/main.txt",
///     "detail": { "expected": "...", "actual": "..." }
///   }
/// }
/// </code>
///
/// Producers that don't follow this shape (legacy errors, malformed JSON, freeform text)
/// return null — the caller treats those as non-refusal errors and does not emit a
/// <see cref="RefusalEvent"/>.
/// </summary>
public static class RefusalPayloadParser
{
    public static ParsedRefusal? TryParse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch
        {
            return null;
        }

        if (root is not JsonObject obj || obj["refusal"] is not JsonObject refusal)
        {
            return null;
        }

        var code = refusal["code"]?.GetValue<string>();
        var reason = refusal["reason"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var axis = refusal["axis"]?.GetValue<string>();
        var path = refusal["path"]?.GetValue<string>();
        var detailNode = refusal["detail"];
        var detailJson = detailNode?.ToJsonString();

        return new ParsedRefusal(code!, reason!, axis, path, detailJson);
    }
}

public sealed record ParsedRefusal(
    string Code,
    string Reason,
    string? Axis,
    string? Path,
    string? DetailJson);
