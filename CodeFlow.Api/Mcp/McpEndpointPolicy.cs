using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Mcp;

/// <summary>
/// Options controlling which MCP server endpoints are accepted at write-time.
///
/// Default posture is strict: only https, no allowlist, no internal targets. Operators relax
/// the policy explicitly via <c>Auth:DevelopmentBypass</c>-style opt-ins in appsettings so the
/// production default fails closed.
/// </summary>
public sealed class McpEndpointPolicyOptions
{
    public const string SectionName = "McpEndpointPolicy";

    /// <summary>
    /// When false (default), endpoints whose host resolves to loopback / link-local / RFC1918
    /// addresses are rejected. Dev/test/docker-compose should set this to true via configuration.
    /// </summary>
    public bool AllowInternalTargets { get; set; }

    /// <summary>
    /// Case-insensitive set of schemes that endpoints may use. Defaults to <c>https</c> only.
    /// </summary>
    public IList<string> AllowedSchemes { get; set; } = new List<string> { "https" };

    /// <summary>
    /// Optional host allowlist. When empty, hosts are not restricted (only scheme and the
    /// internal-target check apply). Each entry is either a literal host or a <c>*.example.com</c>
    /// wildcard for any subdomain.
    /// </summary>
    public IList<string> AllowedHosts { get; set; } = new List<string>();
}

public sealed record McpEndpointPolicyResult(bool IsAllowed, string? Reason);

public interface IMcpEndpointPolicy
{
    ValueTask<McpEndpointPolicyResult> ValidateAsync(Uri endpoint, CancellationToken cancellationToken);
}

public sealed class McpEndpointPolicy : IMcpEndpointPolicy
{
    private readonly IOptionsMonitor<McpEndpointPolicyOptions> optionsMonitor;

    public McpEndpointPolicy(IOptionsMonitor<McpEndpointPolicyOptions> optionsMonitor)
    {
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        this.optionsMonitor = optionsMonitor;
    }

    public async ValueTask<McpEndpointPolicyResult> ValidateAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var options = optionsMonitor.CurrentValue;

        if (!endpoint.IsAbsoluteUri)
        {
            return new McpEndpointPolicyResult(false, "Endpoint must be an absolute URI.");
        }

        if (options.AllowedSchemes.Count > 0
            && !options.AllowedSchemes.Contains(endpoint.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            var allowed = string.Join(", ", options.AllowedSchemes);
            return new McpEndpointPolicyResult(
                false,
                $"Scheme '{endpoint.Scheme}' is not in the allowed set [{allowed}].");
        }

        if (options.AllowedHosts.Count > 0 && !IsHostAllowed(endpoint.Host, options.AllowedHosts))
        {
            return new McpEndpointPolicyResult(
                false,
                $"Host '{endpoint.Host}' is not in the configured allowlist.");
        }

        if (!options.AllowInternalTargets)
        {
            // First check the literal host in case it's a raw IP literal.
            if (IPAddress.TryParse(endpoint.Host, out var literal) && IsInternalAddress(literal))
            {
                return new McpEndpointPolicyResult(
                    false,
                    $"Host '{endpoint.Host}' is an internal/loopback/link-local/private-network address.");
            }

            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
            }
            catch (SocketException ex)
            {
                return new McpEndpointPolicyResult(
                    false,
                    $"DNS resolution failed for '{endpoint.Host}': {ex.Message}");
            }

            foreach (var addr in addresses)
            {
                if (IsInternalAddress(addr))
                {
                    return new McpEndpointPolicyResult(
                        false,
                        $"Host '{endpoint.Host}' resolves to internal/loopback/link-local/private-network address {addr}.");
                }
            }
        }

        return new McpEndpointPolicyResult(true, null);
    }

    private static bool IsHostAllowed(string host, IList<string> allowedHosts)
    {
        foreach (var pattern in allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal)
                && host.Length > pattern.Length - 1
                && host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInternalAddress(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr))
        {
            return true;
        }

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = addr.GetAddressBytes();
            if (bytes[0] == 10) return true;                                          // 10.0.0.0/8
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;      // 172.16.0.0/12
            if (bytes[0] == 192 && bytes[1] == 168) return true;                       // 192.168.0.0/16
            if (bytes[0] == 169 && bytes[1] == 254) return true;                       // 169.254.0.0/16 (link-local, AWS IMDS)
            if (bytes[0] == 127) return true;                                           // 127.0.0.0/8
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;     // 100.64.0.0/10 (CGNAT)
            return false;
        }

        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (addr.IsIPv6LinkLocal) return true;
            if (addr.IsIPv6SiteLocal) return true;
            if (addr.IsIPv4MappedToIPv6)
            {
                return IsInternalAddress(addr.MapToIPv4());
            }
            var bytes = addr.GetAddressBytes();
            if ((bytes[0] & 0xfe) == 0xfc) return true; // fc00::/7 unique local
        }

        return false;
    }
}
