namespace CodeFlow.Api;

public static class CodeFlowApiDefaults
{
    public const string AuthSectionName = "Auth";
    public const string DefaultScheme = "CodeFlow";

    public static class Policies
    {
        public const string Authenticated = "Authenticated";
        public const string AgentsRead = "AgentsRead";
        public const string AgentsWrite = "AgentsWrite";
        public const string WorkflowsRead = "WorkflowsRead";
        public const string WorkflowsWrite = "WorkflowsWrite";
        public const string TracesRead = "TracesRead";
        public const string TracesWrite = "TracesWrite";
        public const string HitlWrite = "HitlWrite";
        public const string OpsRead = "OpsRead";
        public const string OpsWrite = "OpsWrite";
        public const string McpServersRead = "McpServersRead";
        public const string McpServersWrite = "McpServersWrite";
        public const string AgentRolesRead = "AgentRolesRead";
        public const string AgentRolesWrite = "AgentRolesWrite";
        public const string SkillsRead = "SkillsRead";
        public const string SkillsWrite = "SkillsWrite";
        public const string GitHostRead = "GitHostRead";
        public const string GitHostWrite = "GitHostWrite";
        public const string LlmProvidersRead = "LlmProvidersRead";
        public const string LlmProvidersWrite = "LlmProvidersWrite";
        public const string WebSearchProviderRead = "WebSearchProviderRead";
        public const string WebSearchProviderWrite = "WebSearchProviderWrite";
        public const string NotificationsRead = "NotificationsRead";
        public const string NotificationsWrite = "NotificationsWrite";
    }

    /// <summary>
    /// Named bundles of policies used together by certain endpoints. Hoisted out of the
    /// individual endpoint files (F-022 in the 2026-04-28 backend review) so the auth-policy
    /// surface lives next to the policy declarations.
    /// </summary>
    public static class PolicyBundles
    {
        /// <summary>
        /// Workflow-package import endpoints touch every entity kind a package can carry, so
        /// the caller must hold write access to all of them. <see cref="Microsoft.AspNetCore.Builder.AuthorizationEndpointConventionBuilderExtensions.RequireAuthorization(Microsoft.AspNetCore.Builder.IEndpointConventionBuilder, string[])"/>
        /// requires every named policy to pass.
        /// </summary>
        public static readonly string[] PackageImportWrite =
        [
            Policies.WorkflowsWrite,
            Policies.AgentsWrite,
            Policies.AgentRolesWrite,
            Policies.SkillsWrite,
            Policies.McpServersWrite,
        ];
    }

    public static class Permissions
    {
        public const string AgentsRead = "agents:read";
        public const string AgentsWrite = "agents:write";
        public const string WorkflowsRead = "workflows:read";
        public const string WorkflowsWrite = "workflows:write";
        public const string TracesRead = "traces:read";
        public const string TracesWrite = "traces:write";
        public const string HitlWrite = "hitl:write";
        public const string OpsRead = "ops:read";
        public const string OpsWrite = "ops:write";
        public const string McpServersRead = "mcp_servers:read";
        public const string McpServersWrite = "mcp_servers:write";
        public const string AgentRolesRead = "agent_roles:read";
        public const string AgentRolesWrite = "agent_roles:write";
        public const string SkillsRead = "skills:read";
        public const string SkillsWrite = "skills:write";
        public const string GitHostRead = "git_host:read";
        public const string GitHostWrite = "git_host:write";
        public const string LlmProvidersRead = "llm_providers:read";
        public const string LlmProvidersWrite = "llm_providers:write";
        public const string WebSearchProviderRead = "web_search_provider:read";
        public const string WebSearchProviderWrite = "web_search_provider:write";
        public const string NotificationsRead = "notifications:read";
        public const string NotificationsWrite = "notifications:write";
    }

    public static class Roles
    {
        public const string Viewer = "viewer";
        public const string Author = "author";
        public const string Operator = "operator";
        public const string Admin = "admin";
    }

    /// <summary>
    /// Single declarative source for "which roles grant which permissions". AuthOptions inverts
    /// this into the role -> permissions map at startup, so adding a new permission only
    /// requires one edit here (plus the Permissions/Policies constants).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> PermissionRoleMatrix =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Permissions.AgentsRead] = new[] { Roles.Viewer, Roles.Author, Roles.Operator, Roles.Admin },
            [Permissions.AgentsWrite] = new[] { Roles.Author, Roles.Admin },
            [Permissions.WorkflowsRead] = new[] { Roles.Viewer, Roles.Author, Roles.Operator, Roles.Admin },
            [Permissions.WorkflowsWrite] = new[] { Roles.Author, Roles.Admin },
            [Permissions.TracesRead] = new[] { Roles.Viewer, Roles.Author, Roles.Operator, Roles.Admin },
            [Permissions.TracesWrite] = new[] { Roles.Operator, Roles.Admin },
            [Permissions.HitlWrite] = new[] { Roles.Operator, Roles.Admin },
            [Permissions.OpsRead] = new[] { Roles.Operator, Roles.Admin },
            [Permissions.OpsWrite] = new[] { Roles.Operator, Roles.Admin },
            [Permissions.McpServersRead] = new[] { Roles.Viewer, Roles.Author, Roles.Operator, Roles.Admin },
            [Permissions.McpServersWrite] = new[] { Roles.Author, Roles.Admin },
            [Permissions.AgentRolesRead] = new[] { Roles.Viewer, Roles.Author, Roles.Operator, Roles.Admin },
            [Permissions.AgentRolesWrite] = new[] { Roles.Author, Roles.Admin },
            [Permissions.SkillsRead] = new[] { Roles.Viewer, Roles.Author, Roles.Operator, Roles.Admin },
            [Permissions.SkillsWrite] = new[] { Roles.Author, Roles.Admin },
            [Permissions.GitHostRead] = new[] { Roles.Admin },
            [Permissions.GitHostWrite] = new[] { Roles.Admin },
            [Permissions.LlmProvidersRead] = new[] { Roles.Admin },
            [Permissions.LlmProvidersWrite] = new[] { Roles.Admin },
            [Permissions.WebSearchProviderRead] = new[] { Roles.Admin },
            [Permissions.WebSearchProviderWrite] = new[] { Roles.Admin },
            [Permissions.NotificationsRead] = new[] { Roles.Admin },
            [Permissions.NotificationsWrite] = new[] { Roles.Admin },
        };
}
