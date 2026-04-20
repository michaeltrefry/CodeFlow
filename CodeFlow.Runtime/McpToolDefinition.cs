using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

public sealed record McpToolDefinition(
    string Server,
    string ToolName,
    string Description,
    JsonNode? Parameters,
    bool IsMutating = false)
{
    public string FullName => $"mcp:{Server}:{ToolName}";
}
