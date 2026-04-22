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
        this.companyOptions = options.Value.Company;
    }

    public bool HasPermission(ICurrentUser user, string permission)
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
            return fallback.HasPermission(user, permission);
        }

        var permissions = GetOrFetchPermissions(user);
        return permissions.Contains(permission);
    }

    private IReadOnlySet<string> GetOrFetchPermissions(ICurrentUser user)
    {
        var userId = user.Id;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var cacheKey = $"codeflow:company-permissions:{userId}";
        if (cache.TryGetValue<IReadOnlySet<string>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        IReadOnlyList<string> fetched;
        try
        {
            fetched = permissionsApiClient.GetPermissionsAsync(userId!).GetAwaiter().GetResult();
        }
        catch (Exception ex)
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
}
