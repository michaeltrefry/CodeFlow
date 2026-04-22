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

    private async Task<IMcpSession> GetOrOpenSessionAsync(string serverKey, CancellationToken cancellationToken)
    {
        // The Lazy factory is shared across callers, so we must not capture the first caller's
        // cancellation token — if that caller cancels before the handshake completes, every
        // subsequent caller would see a cancelled task for the life of the process. Pass
        // CancellationToken.None into the underlying open call and let each caller apply its own
        // token via WaitAsync below.
        var entry = sessions.GetOrAdd(
            serverKey,
            key => new Lazy<Task<IMcpSession>>(
                () => OpenSessionAsync(key, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await entry.Value.WaitAsync(cancellationToken);
        }
        catch
        {
            // Evict the cached entry when the underlying open task has failed so the next caller
            // retries from scratch instead of seeing the same fault forever. A caller-side
            // cancellation alone (with the inner task still running or completed successfully)
            // must not evict — other callers may be waiting on it.
            if (entry.Value.IsFaulted || entry.Value.IsCanceled)
            {
                sessions.TryRemove(new KeyValuePair<string, Lazy<Task<IMcpSession>>>(serverKey, entry));
            }
            throw;
        }
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
