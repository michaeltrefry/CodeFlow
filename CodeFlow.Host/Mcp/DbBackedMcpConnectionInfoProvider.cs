using CodeFlow.Persistence;
using CodeFlow.Runtime.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Host.Mcp;

/// <summary>
/// Resolves an MCP server's runtime connection info (endpoint, transport, decrypted bearer token)
/// from the <c>mcp_servers</c> admin table. The previous default was
/// <see cref="NullMcpConnectionInfoProvider"/>, which always returned null and made every
/// role-granted MCP tool throw <see cref="McpServerNotConfiguredException"/> at invocation time
/// despite the admin UI showing the server as healthy. The admin UI side bypasses this provider
/// entirely (it calls <see cref="McpToolDiscovery"/> directly with connection info read from the
/// repository) — which is why the gap was invisible until an agent role with MCP grants was wired
/// to the homepage assistant.
/// </summary>
/// <remarks>
/// Singleton because <see cref="IMcpClient"/> is singleton; <see cref="IMcpServerRepository"/> is
/// scoped (it owns a per-request DbContext), so we create a fresh scope on every call. There's no
/// cache here — the admin UI invalidation path doesn't go through us, and connection-info reads
/// are rare (once per session open per server, then the underlying <see cref="DefaultMcpClient"/>
/// keeps the session for the process's lifetime).
/// </remarks>
public sealed class DbBackedMcpConnectionInfoProvider : IMcpConnectionInfoProvider
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<DbBackedMcpConnectionInfoProvider> logger;

    public DbBackedMcpConnectionInfoProvider(
        IServiceScopeFactory scopeFactory,
        ILogger<DbBackedMcpConnectionInfoProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    public async Task<McpServerConnectionInfo?> GetAsync(string serverKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);

        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMcpServerRepository>();

        try
        {
            return await repository.GetConnectionInfoAsync(serverKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to load MCP server '{ServerKey}' connection info from the database. The runtime will treat the server as unconfigured for this request.",
                serverKey);
            return null;
        }
    }
}
