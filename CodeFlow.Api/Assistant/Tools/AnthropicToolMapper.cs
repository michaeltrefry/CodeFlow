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
    /// <summary>
    /// Map the assistant tool registry to Anthropic <see cref="ToolUnion"/>s. When
    /// <paramref name="markLastEphemeral"/> is true, the last entry's underlying <see cref="Tool"/>
    /// is constructed with <c>cache_control: ephemeral</c> — Anthropic caches the entire
    /// tools-array prefix when the marker lands on the last tool, so a single breakpoint suffices.
    /// (<see cref="Tool.CacheControl"/> is init-only, so the marker has to be set at construction
    /// time rather than mutated post-hoc.)
    /// </summary>
    public static IReadOnlyList<ToolUnion> Map(IEnumerable<IAssistantTool> tools, bool markLastEphemeral = false)
    {
        var source = tools as IReadOnlyList<IAssistantTool> ?? tools.ToArray();
        if (source.Count == 0) return Array.Empty<ToolUnion>();

        var result = new ToolUnion[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var isLast = i == source.Count - 1;
            result[i] = MapOne(source[i], applyEphemeral: markLastEphemeral && isLast);
        }
        return result;
    }

    private static ToolUnion MapOne(IAssistantTool tool, bool applyEphemeral)
    {
        return (Tool)(applyEphemeral
            ? new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = BuildInputSchema(tool.InputSchema),
                CacheControl = new CacheControlEphemeral()
            }
            : new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = BuildInputSchema(tool.InputSchema)
            });
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
