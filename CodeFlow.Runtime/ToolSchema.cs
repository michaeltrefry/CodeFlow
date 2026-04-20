using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

public sealed record ToolSchema(
    string Name,
    string Description,
    JsonNode? Parameters,
    bool IsMutating = false);
