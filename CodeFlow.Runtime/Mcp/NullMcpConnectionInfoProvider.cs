namespace CodeFlow.Runtime.Mcp;

public sealed class NullMcpConnectionInfoProvider : IMcpConnectionInfoProvider
{
    public Task<McpServerConnectionInfo?> GetAsync(string serverKey, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<McpServerConnectionInfo?>(null);
    }
}
