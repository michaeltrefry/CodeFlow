using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Mcp;

public sealed class DefaultMcpClient : IMcpClient, IAsyncDisposable
{
    private readonly IMcpConnectionInfoProvider infoProvider;
    private readonly IMcpSessionFactory sessionFactory;
    private readonly ConcurrentDictionary<string, Lazy<Task<IMcpSession>>> sessions = new(StringComparer.OrdinalIgnoreCase);

    private int disposed;

    public DefaultMcpClient(IMcpConnectionInfoProvider infoProvider, IMcpSessionFactory sessionFactory)
    {
        ArgumentNullException.ThrowIfNull(infoProvider);
        ArgumentNullException.ThrowIfNull(sessionFactory);

        this.infoProvider = infoProvider;
        this.sessionFactory = sessionFactory;
    }

    public async Task<McpToolResult> InvokeAsync(
        string server,
        string toolName,
        JsonNode? arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) == 1, this);

        var session = await GetOrOpenSessionAsync(server, cancellationToken);
        return await session.CallToolAsync(toolName, arguments, cancellationToken);
    }

    private Task<IMcpSession> GetOrOpenSessionAsync(string serverKey, CancellationToken cancellationToken)
    {
        var entry = sessions.GetOrAdd(
            serverKey,
            key => new Lazy<Task<IMcpSession>>(
                () => OpenSessionAsync(key, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return entry.Value;
    }

    private async Task<IMcpSession> OpenSessionAsync(string serverKey, CancellationToken cancellationToken)
    {
        var info = await infoProvider.GetAsync(serverKey, cancellationToken)
            ?? throw new McpServerNotConfiguredException(serverKey);

        return await sessionFactory.OpenAsync(info, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        foreach (var entry in sessions.Values)
        {
            IMcpSession? session = null;
            try
            {
                session = await entry.Value;
            }
            catch
            {
                continue;
            }

            try
            {
                await session.DisposeAsync();
            }
            catch
            {
            }
        }

        sessions.Clear();
    }
}
