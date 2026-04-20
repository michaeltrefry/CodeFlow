namespace CodeFlow.Runtime.Mcp;

public sealed record McpServerConnectionInfo(
    string Key,
    Uri Endpoint,
    McpTransportKind Transport,
    string? BearerToken = null);
