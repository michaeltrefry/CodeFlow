namespace CodeFlow.Persistence;

/// <summary>
/// S1 (Workflow Authoring DX): catalog of platform-managed agent roles seeded on startup.
/// Each entry is one row in <c>agent_roles</c> with <see cref="AgentRoleEntity.IsSystemManaged"/>
/// set, plus the corresponding host-tool / MCP-tool grants.
///
/// Seeder semantics live in <see cref="SystemAgentRoleSeeder"/>:
/// <list type="bullet">
///   <item><description>New install: insert role + grants.</description></item>
///   <item><description>Existing role at the same key (system-managed or operator-created):
///   skip — once a row exists the operator owns it, including any subsequent edits to its
///   grants. Catalog changes here only affect fresh installs.</description></item>
/// </list>
/// </summary>
public static class SystemAgentRoles
{
    public const string CodeWorkerKey = "code-worker";
    public const string CodeBuilderKey = "code-builder";
    public const string ReadOnlyShellKey = "read-only-shell";
    public const string CodeFlowAssistantKey = "codeflow-assistant";

    public static readonly IReadOnlyList<SystemAgentRole> All = new[]
    {
        new SystemAgentRole(
            Key: CodeWorkerKey,
            DisplayName: "Code worker",
            Description: "Full read/write filesystem and shell access plus atomic code-aware "
                + "workspace bootstrap (setup_workspace) and PR opening (vcs.open_pr). Use for "
                + "developer agents that clone repos, edit files, run tests, and create commits. "
                + "setup_workspace atomically resolves credentials, clones, discovers the "
                + "authoritative base branch, creates the feature branch, and pushes the empty "
                + "branch to validate auth before any LLM work runs. Idempotent for mid-flow "
                + "repo addition. (sc-683: vcs.clone is no longer granted — setup_workspace "
                + "supersedes it; the tool is kept registered for back-compat with imported "
                + "workflow packages but is marked deprecated in the role editor.) "
                + "bulk_replace handles mechanical N-file renames in one tool call so per-turn "
                + "tool budgets aren't exhausted on broad refactors.",
            Grants: new[]
            {
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "read_file"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "apply_patch"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "bulk_replace"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "run_command"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "setup_workspace"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "vcs.get_repo"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "vcs.open_pr"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "now"),
            }),
        new SystemAgentRole(
            Key: CodeBuilderKey,
            DisplayName: "Code builder",
            Description: "Code-worker capabilities plus containerized build/test (container.run) "
                + "and bounded public web lookup (web_fetch, web_search). Use for developer "
                + "agents that need to build/test in language toolchains the host does not have "
                + "installed. The agent picks an image from Docker Hub (docker.io only); the "
                + "trace's workspace is bind-mounted at /workspace read-write so the agent's "
                + "edits are visible to the build and the build's outputs land back in the "
                + "workspace tree. ABSOLUTE BAN: no repo Dockerfiles, no `docker build`, no "
                + "`docker compose`, no privileged mode, no host networking, no published "
                + "ports, no Docker socket mounts. Web tools are read-only HTTP(S) and never "
                + "send credentials/cookies.",
            Grants: new[]
            {
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "read_file"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "apply_patch"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "bulk_replace"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "run_command"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "setup_workspace"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "vcs.get_repo"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "vcs.open_pr"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "container.run"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "web_fetch"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "web_search"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "now"),
            }),
        new SystemAgentRole(
            Key: ReadOnlyShellKey,
            DisplayName: "Read-only shell",
            Description: "Read filesystem and non-mutating host helpers. Use for inspector / "
                + "reporter agents that should never mutate the workspace.",
            Grants: new[]
            {
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "read_file"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "now"),
            }),
        new SystemAgentRole(
            Key: CodeFlowAssistantKey,
            DisplayName: "CodeFlow assistant",
            Description: "Starter grant set for the homepage assistant when an operator assigns "
                + "a role on the LLM Config page. Tuned for an interactive, user-supervised "
                + "session: read + edit + run + research, but no unattended PR opening "
                + "(vcs.open_pr — the user can click 'Open PR' themselves once they've reviewed "
                + "the change), no setup_workspace (that's a workflow-orchestration tool), and "
                + "no container.run by default (operators who want sandboxed builds can clone "
                + "this role and add it). Web tools are bounded HTTP(S) and never carry "
                + "credentials/cookies. Operators who want MCP server access on top should "
                + "clone the role and add server-specific grants — those are tenant-scoped, "
                + "not platform-managed.",
            Grants: new[]
            {
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "read_file"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "apply_patch"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "bulk_replace"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "run_command"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "web_fetch"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "web_search"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
                new AgentRoleToolGrant(AgentRoleToolCategory.Host, "now"),
            }),
    };
}

public sealed record SystemAgentRole(
    string Key,
    string DisplayName,
    string? Description,
    IReadOnlyList<AgentRoleToolGrant> Grants);
