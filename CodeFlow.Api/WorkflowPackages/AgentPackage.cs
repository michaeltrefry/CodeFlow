namespace CodeFlow.Api.WorkflowPackages;

public static class AgentPackageDefaults
{
    public const string SchemaVersion = "codeflow.agent-package.v1";
}

/// <summary>
/// Self-contained export of a single agent at a pinned version, plus the role / skill /
/// MCP-server closure required to recreate it on a different workspace. Sibling of
/// <see cref="WorkflowPackage"/>; reuses every element record (agent, role, skill, MCP) so
/// the importer can lift the existing per-agent diff/version logic.
/// <para/>
/// AP-1 (sc-832): exactly one entry-point agent per package, but <see cref="Agents"/> is a
/// list so the importer surface stays uniform with workflow packages and a future "agent
/// preset" flow can carry a small dependency closure of its own.
/// </summary>
public sealed record AgentPackage(
    string SchemaVersion,
    WorkflowPackageMetadata Metadata,
    WorkflowPackageReference EntryPoint,
    IReadOnlyList<WorkflowPackageAgent> Agents,
    IReadOnlyList<WorkflowPackageAgentRoleAssignment> AgentRoleAssignments,
    IReadOnlyList<WorkflowPackageRole> Roles,
    IReadOnlyList<WorkflowPackageSkill> Skills,
    IReadOnlyList<WorkflowPackageMcpServer> McpServers,
    AgentPackageManifest? Manifest = null);

/// <summary>
/// Flat at-a-glance summary the editor / package preview surfaces. Mirrors
/// <see cref="WorkflowPackageManifest"/> but with a single entry-point <see cref="Agent"/>
/// instead of a list of workflows.
/// </summary>
public sealed record AgentPackageManifest(
    WorkflowPackageReference Agent,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> McpServers);
