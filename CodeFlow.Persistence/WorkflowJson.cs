using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Persistence;

internal static class WorkflowJson
{
    public static JsonElement? DeserializeDiscriminator(string? discriminatorJson)
    {
        if (string.IsNullOrWhiteSpace(discriminatorJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(discriminatorJson);
        return document.RootElement.Clone();
    }

    public static bool DeepEquals(JsonElement? left, JsonElement? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return JsonNode.DeepEquals(
            JsonNode.Parse(left.Value.GetRawText()),
            JsonNode.Parse(right.Value.GetRawText()));
    }
}
