using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Auth;

public sealed class RoleBasedPermissionChecker : IPermissionChecker
{
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> rolePermissions;

    public RoleBasedPermissionChecker(IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        rolePermissions = BuildRolePermissions(options.Value);
    }

    public bool HasPermission(ICurrentUser user, string permission)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        if (!user.IsAuthenticated)
        {
            return false;
        }

        foreach (var role in user.Roles)
        {
            if (rolePermissions.TryGetValue(role, out var permissions) &&
                permissions.Contains(permission))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildRolePermissions(AuthOptions options)
    {
        var source = options.RolePermissions.Count > 0
            ? options.RolePermissions
            : AuthOptions.DefaultRolePermissions();

        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in source)
        {
            result[pair.Key] = new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }
}
