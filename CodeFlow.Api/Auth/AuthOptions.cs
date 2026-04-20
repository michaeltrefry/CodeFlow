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
        return new Dictionary<string, IList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [CodeFlowApiDefaults.Roles.Viewer] = new List<string>
            {
                CodeFlowApiDefaults.Permissions.AgentsRead,
                CodeFlowApiDefaults.Permissions.WorkflowsRead,
                CodeFlowApiDefaults.Permissions.TracesRead
            },
            [CodeFlowApiDefaults.Roles.Author] = new List<string>
            {
                CodeFlowApiDefaults.Permissions.AgentsRead,
                CodeFlowApiDefaults.Permissions.AgentsWrite,
                CodeFlowApiDefaults.Permissions.WorkflowsRead,
                CodeFlowApiDefaults.Permissions.WorkflowsWrite,
                CodeFlowApiDefaults.Permissions.TracesRead
            },
            [CodeFlowApiDefaults.Roles.Operator] = new List<string>
            {
                CodeFlowApiDefaults.Permissions.AgentsRead,
                CodeFlowApiDefaults.Permissions.WorkflowsRead,
                CodeFlowApiDefaults.Permissions.TracesRead,
                CodeFlowApiDefaults.Permissions.TracesWrite,
                CodeFlowApiDefaults.Permissions.HitlWrite,
                CodeFlowApiDefaults.Permissions.OpsRead,
                CodeFlowApiDefaults.Permissions.OpsWrite
            },
            [CodeFlowApiDefaults.Roles.Admin] = new List<string>
            {
                CodeFlowApiDefaults.Permissions.AgentsRead,
                CodeFlowApiDefaults.Permissions.AgentsWrite,
                CodeFlowApiDefaults.Permissions.WorkflowsRead,
                CodeFlowApiDefaults.Permissions.WorkflowsWrite,
                CodeFlowApiDefaults.Permissions.TracesRead,
                CodeFlowApiDefaults.Permissions.TracesWrite,
                CodeFlowApiDefaults.Permissions.HitlWrite,
                CodeFlowApiDefaults.Permissions.OpsRead,
                CodeFlowApiDefaults.Permissions.OpsWrite
            }
        };
    }
}
