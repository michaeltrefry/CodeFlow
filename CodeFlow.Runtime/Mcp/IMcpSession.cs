using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Mcp;

public interface IMcpSession : IAsyncDisposable
{
    Task<McpToolResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default);
}
