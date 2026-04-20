using System.Net;
using System.Net.Http;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CodeFlow.Runtime.Mcp;

public sealed class McpToolDiscovery
{
    private readonly IMcpSessionFactory sessionFactory;

    public McpToolDiscovery(IMcpSessionFactory sessionFactory)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        this.sessionFactory = sessionFactory;
    }

    public async Task<McpDiscoveryResult> DiscoverAsync(
        McpServerConnectionInfo info,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        IMcpSession? session = null;
        try
        {
            session = await sessionFactory.OpenAsync(info, cancellationToken);
            var tools = await session.ListToolsAsync(cancellationToken);
            return new McpDiscoverySuccess(tools);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ClassifyFailure(ex);
        }
        finally
        {
            if (session is not null)
            {
                try { await session.DisposeAsync(); } catch { }
            }
        }
    }

    private static McpDiscoveryFailure ClassifyFailure(Exception ex)
    {
        return ex switch
        {
            McpException mcp when IsProtocolMismatch(mcp) =>
                new McpDiscoveryFailure(McpDiscoveryErrorKind.ProtocolMismatch, mcp.Message, mcp),

            McpException mcp =>
                new McpDiscoveryFailure(McpDiscoveryErrorKind.Handshake, mcp.Message, mcp),

            HttpRequestException http when IsAuthStatus(http.StatusCode) =>
                new McpDiscoveryFailure(McpDiscoveryErrorKind.Authentication, http.Message, http),

            HttpRequestException http =>
                new McpDiscoveryFailure(McpDiscoveryErrorKind.Unreachable, http.Message, http),

            TaskCanceledException timeout =>
                new McpDiscoveryFailure(McpDiscoveryErrorKind.Unreachable, "MCP server did not respond within the timeout window.", timeout),

            TimeoutException timeout =>
                new McpDiscoveryFailure(McpDiscoveryErrorKind.Unreachable, timeout.Message, timeout),

            JsonException json =>
                new McpDiscoveryFailure(McpDiscoveryErrorKind.Handshake, "MCP server returned a malformed JSON response.", json),

            _ => new McpDiscoveryFailure(McpDiscoveryErrorKind.Handshake, ex.Message, ex),
        };
    }

    private static bool IsAuthStatus(HttpStatusCode? status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool IsProtocolMismatch(McpException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("protocol version", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported protocol", StringComparison.OrdinalIgnoreCase);
    }
}
