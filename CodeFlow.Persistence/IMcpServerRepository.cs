using CodeFlow.Runtime.Mcp;

namespace CodeFlow.Persistence;

public interface IMcpServerRepository
{
    Task<IReadOnlyList<McpServer>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default);

    Task<McpServer?> GetAsync(long id, CancellationToken cancellationToken = default);

    Task<McpServer?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    Task<long> CreateAsync(McpServerCreate create, CancellationToken cancellationToken = default);

    Task UpdateAsync(long id, McpServerUpdate update, CancellationToken cancellationToken = default);

    Task ArchiveAsync(long id, CancellationToken cancellationToken = default);

    Task UpdateHealthAsync(
        long id,
        McpServerHealthStatus status,
        DateTime? lastVerifiedAtUtc,
        string? lastError,
        CancellationToken cancellationToken = default);

    Task ReplaceToolsAsync(
        long id,
        IReadOnlyList<McpServerToolWrite> tools,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpServerTool>> GetToolsAsync(long id, CancellationToken cancellationToken = default);

    Task<McpServerConnectionInfo?> GetConnectionInfoAsync(string serverKey, CancellationToken cancellationToken = default);
}
