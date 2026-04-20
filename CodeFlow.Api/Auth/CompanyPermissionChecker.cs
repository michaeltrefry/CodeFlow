using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Auth;

/// <summary>
/// IPermissionChecker adapter for the company-specific PermissionsApi + IdentityServer stack.
/// Fetches a user's permission set from PermissionsApi and caches it per user for a short window.
/// Falls back to role-based checks when PermissionsApi has not been configured so dev environments
/// can run without the real backend.
/// </summary>
public sealed class CompanyPermissionChecker : IPermissionChecker
{
    private readonly IPermissionsApiClient permissionsApiClient;
    private readonly RoleBasedPermissionChecker fallback;
    private readonly IMemoryCache cache;
    private readonly CompanyAuthOptions companyOptions;

    public CompanyPermissionChecker(
        IPermissionsApiClient permissionsApiClient,
        RoleBasedPermissionChecker fallback,
        IMemoryCache cache,
        IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(permissionsApiClient);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);

        this.permissionsApiClient = permissionsApiClient;
        this.fallback = fallback;
        this.cache = cache;
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

        if (string.IsNullOrWhiteSpace(companyOptions.PermissionsApiBaseUrl))
        {
            return fallback.HasPermission(user, permission);
        }

        var permissions = GetOrFetchPermissions(user);

        if (permissions.Contains(permission))
        {
            return true;
        }

        // When PermissionsApi returns an empty set it signals either "no permissions" or a transient outage.
        // Degrade gracefully by deferring to role-based defaults so operators aren't locked out in degraded mode.
        if (permissions.Count == 0)
        {
            return fallback.HasPermission(user, permission);
        }

        return false;
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

        var fetchTask = permissionsApiClient.GetPermissionsAsync(userId!);
        var fetched = fetchTask.GetAwaiter().GetResult();
        var set = new HashSet<string>(fetched, StringComparer.OrdinalIgnoreCase);

        var ttl = TimeSpan.FromSeconds(Math.Max(1, companyOptions.PermissionsCacheSeconds));
        cache.Set<IReadOnlySet<string>>(cacheKey, set, ttl);
        return set;
    }
}
