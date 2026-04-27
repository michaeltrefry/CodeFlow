namespace CodeFlow.Persistence;

/// <summary>
/// S1 (Workflow Authoring DX): catalog of platform-managed agent roles seeded on startup.
/// Each entry is one row in <c>agent_roles</c> with <see cref="AgentRoleEntity.IsSystemManaged"/>
/// set, plus the corresponding host-tool / MCP-tool grants.
///
/// Seeder semantics live in <see cref="SystemAgentRoleSeeder"/>:
/// <list type="bullet">
///   <item><description>New install: insert role + grants.</description></item>
///   <item><description>Existing system-managed role: re-sync grants so platform updates flow.</description></item>
///   <item><description>Existing user role at the same key: skip (collision strategy — operators
///   keep their custom role; the system variant is not seeded).</description></item>
/// </list>
/// </summary>
public static class SystemAgentRoles
{
    public const string CodeWorkerKey = "code-worker";
    public const string ReadOnlyShellKey = "read-only-shell";
    public const string KanbanWorkerKey = "kanban-worker";

    /// <summary>
    /// Conventional MCP server key the kanban-worker role grants assume. If a tenant registers
    /// the kanban MCP under a different server key, the operator must adjust the grants.
    /// </summary>
    public const string KanbanMcpServerKey = "kanban";

    public static readonly IReadOnlyList<SystemAgentRole> All = new[]
    {
        new SystemAgentRole(
            Key: CodeWorkerKey,
            DisplayName: "Code worker",
            Description: "Full read/write filesystem and shell access. Use for developer agents "
                + "that clone repos, edit files, run tests, and create commits.",
            Grants: new[]
            {
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "read_file"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "apply_patch"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "run_command"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "now"),
            }),
        new SystemAgentRole(
            Key: ReadOnlyShellKey,
            DisplayName: "Read-only shell",
            Description: "Read filesystem and execute shell commands. Use for inspector / "
                + "reporter agents that should never mutate the workspace.",
            Grants: new[]
            {
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "read_file"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "run_command"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "now"),
            }),
        new SystemAgentRole(
            Key: KanbanWorkerKey,
            DisplayName: "Kanban worker",
            Description: "Read/write access to the conventional kanban MCP server (key: "
                + $"'{KanbanMcpServerKey}'). Use for project-management / PM agents that track "
                + "tasks. If the operator registers the kanban MCP under a different key, "
                + "edit the grants on this role to match.",
            Grants: new[]
            {
                new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{KanbanMcpServerKey}:list_projects"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{KanbanMcpServerKey}:create_project"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{KanbanMcpServerKey}:list_epics"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{KanbanMcpServerKey}:get_epic"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{KanbanMcpServerKey}:create_epic"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{KanbanMcpServerKey}:update_epic"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{KanbanMcpServerKey}:list_work_items"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{KanbanMcpServerKey}:list_epic_work_items"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{KanbanMcpServerKey}:create_work_item"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{KanbanMcpServerKey}:update_work_item"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{KanbanMcpServerKey}:move_work_item"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, $"mcp:{KanbanMcpServerKey}:add_work_item_comment"),
            }),
    };
}

public sealed record SystemAgentRole(
    string Key,
    string DisplayName,
    string? Description,
    IReadOnlyList<AgentRoleToolGrant> Grants);
