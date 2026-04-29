using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Auth;

/// <summary>
/// IPermissionChecker adapter for the company-specific PermissionsApi + IdentityServer stack.
/// Fetches a user's permission set from PermissionsApi and caches it per user for a short window.
/// Falls back to role-based checks only when PermissionsApi has not been configured (dev/local
/// environments); when configured, the PermissionsApi result is authoritative and any failure
/// or empty result fails closed.
/// </summary>
public sealed class CompanyPermissionChecker : IPermissionChecker
{
    private readonly IPermissionsApiClient permissionsApiClient;
    private readonly RoleBasedPermissionChecker fallback;
    private readonly IMemoryCache cache;
    private readonly ILogger<CompanyPermissionChecker> logger;
    private readonly CompanyAuthOptions companyOptions;
    private readonly string authorityHash;

    public CompanyPermissionChecker(
        IPermissionsApiClient permissionsApiClient,
        RoleBasedPermissionChecker fallback,
        IMemoryCache cache,
        ILogger<CompanyPermissionChecker> logger,
        IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(permissionsApiClient);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        this.permissionsApiClient = permissionsApiClient;
        this.fallback = fallback;
        this.cache = cache;
        this.logger = logger;
        var auth = options.Value;
        this.companyOptions = auth.Company;
        this.authorityHash = ComputeAuthorityHash(auth.Authority, auth.Audience);
    }

    public async Task<bool> HasPermissionAsync(ICurrentUser user, string permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        if (!user.IsAuthenticated)
        {
            return false;
        }

        // Explicit dev/local fallback path: PermissionsApi is not configured, so the role map is
        // the only source of permissions. This is the only code path that may use the fallback.
        if (string.IsNullOrWhiteSpace(companyOptions.PermissionsApiBaseUrl))
        {
            return await fallback.HasPermissionAsync(user, permission, cancellationToken);
        }

        var permissions = await GetOrFetchPermissionsAsync(user, cancellationToken);
        return permissions.Contains(permission);
    }

    private async Task<IReadOnlySet<string>> GetOrFetchPermissionsAsync(ICurrentUser user, CancellationToken cancellationToken)
    {
        var userId = user.Id;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Include an authority-hash prefix so identical `sub` claims asserted by different issuers
        // (e.g. staging vs prod, or two tenants behind one CodeFlow instance) never collide.
        var cacheKey = $"codeflow:company-permissions:{authorityHash}:{userId}";
        if (cache.TryGetValue<IReadOnlySet<string>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        IReadOnlyList<string> fetched;
        try
        {
            fetched = await permissionsApiClient.GetPermissionsAsync(userId!, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "PermissionsApi call failed for user {UserId}; denying permission request (fail-closed).",
                userId);
            // Do not cache failures — next request should retry the backend.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var set = new HashSet<string>(fetched, StringComparer.OrdinalIgnoreCase);

        var ttl = TimeSpan.FromSeconds(Math.Max(1, companyOptions.PermissionsCacheSeconds));
        cache.Set<IReadOnlySet<string>>(cacheKey, set, ttl);
        return set;
    }

    private static string ComputeAuthorityHash(string? authority, string? audience)
    {
        var material = $"{authority ?? string.Empty}|{audience ?? string.Empty}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}
