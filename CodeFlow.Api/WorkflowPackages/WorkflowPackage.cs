using CodeFlow.Persistence;
using CodeFlow.Runtime.Mcp;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.WorkflowPackages;

public static class WorkflowPackageDefaults
{
    public const string SchemaVersion = "codeflow.workflow-package.v1";
}

public sealed record WorkflowPackage(
    string SchemaVersion,
    WorkflowPackageMetadata Metadata,
    WorkflowPackageReference EntryPoint,
    IReadOnlyList<WorkflowPackageWorkflow> Workflows,
    IReadOnlyList<WorkflowPackageAgent> Agents,
    IReadOnlyList<WorkflowPackageAgentRoleAssignment> AgentRoleAssignments,
    IReadOnlyList<WorkflowPackageRole> Roles,
    IReadOnlyList<WorkflowPackageSkill> Skills,
    IReadOnlyList<WorkflowPackageMcpServer> McpServers,
    WorkflowPackageManifest? Manifest = null);

public sealed record WorkflowPackageMetadata(
    string ExportedFrom,
    DateTime ExportedAtUtc);

/// <summary>
/// Flat enumeration of every (key, version) included in the package. The same data is implied
/// by the typed collections on <see cref="WorkflowPackage"/>; the manifest is a single
/// at-a-glance summary the editor's package preview surfaces (V8 / R5.5).
/// </summary>
/// <param name="Workflows">Every workflow included by transitive subflow expansion (entry
/// point first, sorted by key/version after).</param>
/// <param name="Agents">Every agent referenced by a node in any included workflow at its
/// pinned version.</param>
/// <param name="Roles">Every role assigned to any included agent. Versions are absent — roles
/// are unversioned.</param>
/// <param name="Skills">Every skill granted by any included role. Versions absent.</param>
/// <param name="McpServers">Every MCP server granted to any included role. Versions absent.</param>
public sealed record WorkflowPackageManifest(
    IReadOnlyList<WorkflowPackageReference> Workflows,
    IReadOnlyList<WorkflowPackageReference> Agents,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> McpServers);

public sealed record WorkflowPackageReference(
    string Key,
    int Version);

public sealed record WorkflowPackageWorkflow(
    string Key,
    int Version,
    string Name,
    int MaxRoundsPerRound,
    WorkflowCategory Category,
    IReadOnlyList<string> Tags,
    DateTime CreatedAtUtc,
    IReadOnlyList<WorkflowPackageWorkflowNode> Nodes,
    IReadOnlyList<WorkflowPackageWorkflowEdge> Edges,
    IReadOnlyList<WorkflowPackageWorkflowInput> Inputs);

public sealed record WorkflowPackageWorkflowNode(
    Guid Id,
    WorkflowNodeKind Kind,
    string? AgentKey,
    int? AgentVersion,
    string? OutputScript,
    IReadOnlyList<string> OutputPorts,
    double LayoutX,
    double LayoutY,
    string? SubflowKey = null,
    int? SubflowVersion = null,
    int? ReviewMaxRounds = null,
    string? LoopDecision = null,
    string? InputScript = null,
    bool OptOutLastRoundReminder = false);

public sealed record WorkflowPackageWorkflowEdge(
    Guid FromNodeId,
    string FromPort,
    Guid ToNodeId,
    string ToPort,
    bool RotatesRound,
    int SortOrder);

public sealed record WorkflowPackageWorkflowInput(
    string Key,
    string DisplayName,
    WorkflowInputKind Kind,
    bool Required,
    string? DefaultValueJson,
    string? Description,
    int Ordinal);

public sealed record WorkflowPackageAgent(
    string Key,
    int Version,
    AgentKind Kind,
    JsonNode? Config,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    IReadOnlyList<WorkflowPackageAgentOutput> Outputs);

public sealed record WorkflowPackageAgentOutput(
    string Kind,
    string? Description,
    JsonNode? PayloadExample);

public sealed record WorkflowPackageAgentRoleAssignment(
    string AgentKey,
    IReadOnlyList<string> RoleKeys);

public sealed record WorkflowPackageRole(
    string Key,
    string DisplayName,
    string? Description,
    bool IsArchived,
    IReadOnlyList<WorkflowPackageRoleGrant> ToolGrants,
    IReadOnlyList<string> SkillNames);

public sealed record WorkflowPackageRoleGrant(
    AgentRoleToolCategory Category,
    string ToolIdentifier);

public sealed record WorkflowPackageSkill(
    string Name,
    string Body,
    bool IsArchived,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    DateTime UpdatedAtUtc,
    string? UpdatedBy);

public sealed record WorkflowPackageMcpServer(
    string Key,
    string DisplayName,
    McpTransportKind Transport,
    string EndpointUrl,
    bool HasBearerToken,
    McpServerHealthStatus HealthStatus,
    DateTime? LastVerifiedAtUtc,
    string? LastVerificationError,
    bool IsArchived,
    IReadOnlyList<WorkflowPackageMcpServerTool> Tools);

public sealed record WorkflowPackageMcpServerTool(
    string ToolName,
    string? Description,
    JsonNode? Parameters,
    bool IsMutating,
    DateTime SyncedAtUtc);
