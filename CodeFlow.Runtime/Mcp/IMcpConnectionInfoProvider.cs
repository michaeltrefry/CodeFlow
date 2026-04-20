namespace CodeFlow.Runtime.Mcp;

public interface IMcpConnectionInfoProvider
{
    Task<McpServerConnectionInfo?> GetAsync(string serverKey, CancellationToken cancellationToken = default);
}
