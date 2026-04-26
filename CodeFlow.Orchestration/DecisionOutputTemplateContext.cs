using System.Text.Json;
using Scriban.Runtime;

namespace CodeFlow.Orchestration;

/// <summary>
/// Builds the Scriban <see cref="ScriptObject"/> exposed to decision-output templates.
/// Kept separate from <c>ContextAssembler</c> because prompt templates flatten to dotted
/// string keys, while decision templates receive structured JSON directly.
/// </summary>
public static class DecisionOutputTemplateContext
{
    public static ScriptObject Build(
        string decision,
        string outputPortName,
        string outputText,
        JsonElement? outputJson,
        JsonElement? inputJson,
        IReadOnlyDictionary<string, JsonElement> contextInputs,
        IReadOnlyDictionary<string, JsonElement> workflowInputs)
    {
        var scope = new ScriptObject
        {
            ["decision"] = decision,
            ["outputPortName"] = outputPortName,
            // The raw submitted text is always available as a string. When the submission parses
            // as a JSON object or array, `output` shadows the string with the structured form so
            // templates can write `{{ output.field }}`. Plain text / scalars stay as strings.
            ["output"] = ConvertValue(outputJson) ?? outputText
        };

        scope["input"] = ConvertValue(inputJson) ?? new ScriptObject();
        scope["context"] = BuildNamespace(contextInputs);
        scope["workflow"] = BuildNamespace(workflowInputs);

        return scope;
    }

    /// <summary>
    /// Build the render scope for a HITL submission. Form-field values land under
    /// <c>input.*</c> (each field keyed by its name). HITL-only extras from the decision payload
    /// — <c>reason</c>, <c>reasons</c>, <c>actions</c> — sit at the top level so templates can
    /// reference them directly.
    /// </summary>
    public static ScriptObject BuildForHitl(
        string decision,
        string outputPortName,
        IReadOnlyDictionary<string, JsonElement> fieldValues,
        string? reason,
        IReadOnlyList<string>? reasons,
        IReadOnlyList<string>? actions,
        IReadOnlyDictionary<string, JsonElement> contextInputs,
        IReadOnlyDictionary<string, JsonElement> workflowInputs)
    {
        var scope = new ScriptObject
        {
            ["decision"] = decision,
            ["outputPortName"] = outputPortName,
            ["input"] = BuildNamespace(fieldValues),
            ["reason"] = reason ?? string.Empty,
            ["reasons"] = reasons is null ? new ScriptArray() : ToScriptArray(reasons),
            ["actions"] = actions is null ? new ScriptArray() : ToScriptArray(actions),
            ["context"] = BuildNamespace(contextInputs),
            ["workflow"] = BuildNamespace(workflowInputs)
        };

        return scope;
    }

    private static ScriptArray ToScriptArray(IReadOnlyList<string> values)
    {
        var array = new ScriptArray();
        foreach (var value in values)
        {
            array.Add(value);
        }
        return array;
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
