namespace CodeFlow.Runtime.Mcp;

public abstract record McpDiscoveryResult;

public sealed record McpDiscoverySuccess(
    IReadOnlyList<McpToolDefinition> Tools,
    string? ProtocolVersion = null) : McpDiscoveryResult;

public sealed record McpDiscoveryFailure(
    McpDiscoveryErrorKind Kind,
    string Message,
    Exception? Cause = null) : McpDiscoveryResult;

public enum McpDiscoveryErrorKind
{
    Unreachable,
    Authentication,
    Handshake,
    ProtocolMismatch,
}
