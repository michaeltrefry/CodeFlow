namespace CodeFlow.Runtime.Mcp;

public sealed class McpServerNotConfiguredException : Exception
{
    public McpServerNotConfiguredException(string serverKey)
        : base($"No MCP server configured with key '{serverKey}'.")
    {
        ServerKey = serverKey;
    }

    public string ServerKey { get; }
}
