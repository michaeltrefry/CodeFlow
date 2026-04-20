using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

public sealed record ToolCall(
    string Id,
    string Name,
    JsonNode? Arguments);
