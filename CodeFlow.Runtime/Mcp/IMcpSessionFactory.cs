namespace CodeFlow.Runtime.Mcp;

public interface IMcpSessionFactory
{
    Task<IMcpSession> OpenAsync(McpServerConnectionInfo info, CancellationToken cancellationToken = default);
}
