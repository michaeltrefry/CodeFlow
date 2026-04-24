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
        };
}
