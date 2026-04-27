using System.Text.Json;
using Scriban.Runtime;

namespace CodeFlow.Orchestration;

/// <summary>
/// Builds the Scriban <see cref="ScriptObject"/> exposed to a Transform node's template render.
/// The scope is intentionally minimal: <c>input.*</c> (the upstream structured artifact),
/// <c>context.*</c> (workflow-local inputs), and <c>workflow.*</c> (workflow-global inputs).
/// Same JSON-to-Scriban conversion semantics as <see cref="DecisionOutputTemplateContext"/>;
/// kept in a separate type so the namespaces don't accidentally drift.
/// </summary>
public static class TransformNodeContext
{
    public static ScriptObject Build(
        JsonElement inputJson,
        IReadOnlyDictionary<string, JsonElement> contextInputs,
        IReadOnlyDictionary<string, JsonElement> workflowInputs)
    {
        var scope = new ScriptObject
        {
            ["input"] = ConvertValue(inputJson) ?? new ScriptObject(),
            ["context"] = BuildNamespace(contextInputs),
            ["workflow"] = BuildNamespace(workflowInputs),
        };
        return scope;
    }

    private static ScriptObject BuildNamespace(IReadOnlyDictionary<string, JsonElement> entries)
    {
        var scope = new ScriptObject();
        foreach (var (key, element) in entries)
        {
            scope[key] = ConvertValue(element) ?? string.Empty;
        }
        return scope;
    }

    private static object? ConvertValue(JsonElement? maybeElement)
    {
        if (maybeElement is null)
        {
            return null;
        }

        var element = maybeElement.Value;
        return element.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(element),
            JsonValueKind.Array => ConvertArray(element),
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.TryGetInt64(out var i) ? i : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private static ScriptObject ConvertObject(JsonElement element)
    {
        var result = new ScriptObject();
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ConvertValue(property.Value);
        }
        return result;
    }

    private static ScriptArray ConvertArray(JsonElement element)
    {
        var result = new ScriptArray();
        foreach (var item in element.EnumerateArray())
        {
            result.Add(ConvertValue(item));
        }
        return result;
    }
}
