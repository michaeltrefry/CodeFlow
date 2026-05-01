using System.Net;
using System.Net.Sockets;

namespace CodeFlow.Runtime.Web;

/// <summary>
/// SSRF defense for the web_search/web_fetch host tools. Two checkpoints: literal URL
/// validation (catches the obvious cases like <c>http://localhost</c> or
/// <c>http://10.0.0.1</c> before any DNS work happens), and resolved-IP validation (catches
/// hostnames that resolve to private/loopback/link-local/metadata addresses including the
/// AWS/GCP metadata service at 169.254.169.254). Callers run both before issuing a request
/// AND on every redirect Location, since redirects can cross from public to private space.
/// </summary>
public static class UrlPolicy
{
    public static UrlValidationResult ValidateLiteralUrl(WebToolOptions options, string urlString)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(urlString))
        {
            return UrlValidationResult.Deny("url-required", "URL is required.");
        }

        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
        {
            return UrlValidationResult.Deny("url-invalid", "URL must be an absolute http(s) URL.");
        }

        if (!options.AllowedSchemes.Any(s => string.Equals(s, uri.Scheme, StringComparison.OrdinalIgnoreCase)))
        {
            return UrlValidationResult.Deny(
                "url-scheme-denied",
                $"Scheme '{uri.Scheme}' is not allowed; only {string.Join(", ", options.AllowedSchemes)} are accepted.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return UrlValidationResult.Deny(
                "url-credentials-denied",
                "URLs containing userinfo (user:password@) are not allowed; web tools never send credentials.");
        }

        if (!options.BlockPrivateNetworks)
        {
            return UrlValidationResult.Allow(uri);
        }

        var host = uri.Host;
        if (IsLiteralPrivateHost(host, out var literalReason))
        {
            return UrlValidationResult.Deny("url-private-host", literalReason);
        }

        return UrlValidationResult.Allow(uri);
    }

    public static UrlValidationResult ValidateResolvedAddresses(
        WebToolOptions options,
        Uri uri,
        IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(addresses);

        if (!options.BlockPrivateNetworks)
        {
            return UrlValidationResult.Allow(uri);
        }

        if (addresses.Count == 0)
        {
            return UrlValidationResult.Deny(
                "dns-failed",
                $"Hostname '{uri.Host}' did not resolve to any IP address.");
        }

        foreach (var ip in addresses)
        {
            if (IsPrivateAddress(ip))
            {
                return UrlValidationResult.Deny(
                    "url-private-host",
                    $"Hostname '{uri.Host}' resolves to private/loopback/link-local address {ip}.");
            }
        }

        return UrlValidationResult.Allow(uri);
    }

    public static bool IsLiteralPrivateHost(string host, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(host))
        {
            reason = "Host is empty.";
            return true;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Host '{host}' is a loopback name.";
            return true;
        }

        // mDNS .local names and *.internal/*.intranet typically resolve only inside private
        // networks; refuse them out of the gate so the agent can't accidentally probe an
        // intranet via DNS rebinding or split-horizon DNS.
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".intranet", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".corp", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".private", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Host '{host}' uses an internal-network suffix.";
            return true;
        }

        var trimmed = host.Trim('[', ']');
        if (IPAddress.TryParse(trimmed, out var ip) && IsPrivateAddress(ip))
        {
            reason = $"Host '{host}' is a private/loopback/link-local IP literal.";
            return true;
        }

        return false;
    }

    public static bool IsPrivateAddress(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // 0.0.0.0/8 - "this network"
            if (bytes[0] == 0) return true;
            // 10.0.0.0/8 - private
            if (bytes[0] == 10) return true;
            // 100.64.0.0/10 - CGNAT
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
            // 169.254.0.0/16 - link-local (covers AWS/GCP metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 172.16.0.0/12 - private
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16 - private
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 224.0.0.0/4 - multicast and 240.0.0.0/4 reserved
            if (bytes[0] >= 224) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv4-mapped IPv6 (::ffff:a.b.c.d) — re-check against IPv4 rules.
            if (ip.IsIPv4MappedToIPv6)
            {
                return IsPrivateAddress(ip.MapToIPv4());
            }

            var bytes = ip.GetAddressBytes();
            // fc00::/7 - unique local addresses
            if ((bytes[0] & 0xfe) == 0xfc) return true;
            // fe80::/10 - link-local
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return true;
            // ::/128 unspecified is treated as loopback above; ::1 is loopback handled above too.
            // Multicast ff00::/8
            if (bytes[0] == 0xff) return true;
            return false;
        }

        // Unknown address family; refuse by default — we only support IPv4 and IPv6.
        return true;
    }
}

public sealed record UrlValidationResult(bool Allowed, Uri? Uri, string? Code, string? Reason)
{
    public static UrlValidationResult Allow(Uri uri) => new(true, uri, null, null);

    public static UrlValidationResult Deny(string code, string reason) => new(false, null, code, reason);
}
