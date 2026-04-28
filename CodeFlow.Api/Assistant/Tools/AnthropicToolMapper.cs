using System.Text.Json;
using Anthropic.Models.Messages;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Translates the assistant tool registry to the Anthropic SDK's <see cref="ToolUnion"/> shape and
/// builds the <see cref="InputSchema"/> from the tool's JSON Schema. Lives apart from
/// <c>CodeFlowAssistant</c> so the SDK-specific shape doesn't leak into the chat-loop code.
/// </summary>
internal static class AnthropicToolMapper
{
    public static IReadOnlyList<ToolUnion> Map(IEnumerable<IAssistantTool> tools)
    {
        return tools.Select(MapOne).ToArray();
    }

    private static ToolUnion MapOne(IAssistantTool tool)
    {
        return (Tool)new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = BuildInputSchema(tool.InputSchema)
        };
    }

    private static InputSchema BuildInputSchema(JsonElement schema)
    {
        // The Anthropic SDK's InputSchema model has init-only properties (Type, Properties,
        // Required) plus a RawData escape hatch that the wire serializer pulls verbatim. Going
        // through FromRawUnchecked lets us pass an arbitrary draft-07 schema (additionalProperties,
        // enum, nested objects, etc.) without flattening through the SDK's typed surface, which
        // only models a top-level {type, properties, required} object.
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Tool input schema must be a JSON object.");
        }

        var rawData = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in schema.EnumerateObject())
        {
            rawData[prop.Name] = prop.Value.Clone();
        }

        return InputSchema.FromRawUnchecked(rawData);
    }
}
