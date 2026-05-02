namespace CodeFlow.Api.Auth;

public enum AuthMode
{
    Generic = 0,
    Company = 1
}

public sealed class CompanyAuthOptions
{
    public string? PermissionsApiBaseUrl { get; set; }

    public string? PermissionsApiApiKey { get; set; }

    public int PermissionsCacheSeconds { get; set; } = 60;
}

public sealed class AuthOptions
{
    public AuthMode Mode { get; set; } = AuthMode.Generic;

    public CompanyAuthOptions Company { get; set; } = new();

    public string? Authority { get; set; }

    public string? Audience { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    public string NameClaim { get; set; } = "name";

    public string EmailClaim { get; set; } = "email";

    public string SubjectClaim { get; set; } = "sub";

    public string RolesClaim { get; set; } = "roles";

    public bool DevelopmentBypass { get; set; }

    /// <summary>
    /// Public OAuth client id advertised to first-run CLI clients via
    /// <c>GET /api/auth/config</c>. Defaults to <c>codeflow-cli</c> so a stock
    /// Keycloak realm following the CodeFlow convention works without extra config.
    /// </summary>
    public string CliClientId { get; set; } = "codeflow-cli";

    /// <summary>
    /// Space-separated OAuth scopes the CLI should request, advertised via
    /// <c>GET /api/auth/config</c>. Defaults to the standard OIDC profile so the
    /// CLI receives an id token plus name/email claims.
    /// </summary>
    public string CliScopes { get; set; } = "openid profile email";

    public string DevelopmentUserId { get; set; } = "local-dev";

    public string DevelopmentUserEmail { get; set; } = "local-dev@codeflow.local";

    public string DevelopmentUserName { get; set; } = "Local Developer";

    public IList<string> DevelopmentRoles { get; set; } = new List<string>
    {
        CodeFlowApiDefaults.Roles.Admin
    };

    public IDictionary<string, IList<string>> RolePermissions { get; set; } = DefaultRolePermissions();

    public static IDictionary<string, IList<string>> DefaultRolePermissions()
    {
        // Build role -> permissions by inverting the permission -> roles matrix so there's a
        // single authoritative source of "which roles can do what". Adding a new permission
        // only requires touching PermissionRoleMatrix.
        var rolePermissions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [CodeFlowApiDefaults.Roles.Viewer] = new(),
            [CodeFlowApiDefaults.Roles.Author] = new(),
            [CodeFlowApiDefaults.Roles.Operator] = new(),
            [CodeFlowApiDefaults.Roles.Admin] = new(),
        };

        foreach (var (permission, roles) in CodeFlowApiDefaults.PermissionRoleMatrix)
        {
            foreach (var role in roles)
            {
                if (rolePermissions.TryGetValue(role, out var list))
                {
                    list.Add(permission);
                }
            }
        }

        return rolePermissions.ToDictionary(
            pair => pair.Key,
            pair => (IList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }
}
