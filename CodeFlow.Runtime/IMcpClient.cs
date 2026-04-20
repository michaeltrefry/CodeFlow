using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

public interface IMcpClient
{
    Task<McpToolResult> InvokeAsync(
        string server,
        string toolName,
        JsonNode? arguments,
        CancellationToken cancellationToken = default);
}
