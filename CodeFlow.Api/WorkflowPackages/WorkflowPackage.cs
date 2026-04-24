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
    IReadOnlyList<WorkflowPackageMcpServer> McpServers);

public sealed record WorkflowPackageMetadata(
    string ExportedFrom,
    DateTime ExportedAtUtc);

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
    string? Script,
    IReadOnlyList<string> OutputPorts,
    double LayoutX,
    double LayoutY,
    string? SubflowKey = null,
    int? SubflowVersion = null,
    int? ReviewMaxRounds = null,
    string? LoopDecision = null);

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
