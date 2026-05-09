using CodeFlow.Api.Mcp;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.WorkflowPackages.Admission;
using CodeFlow.Persistence;

namespace CodeFlow.Api.WorkflowPackages;

/// <summary>
/// AP-2 (sc-833): preview / apply path for agent-only packages.
/// <para/>
/// Implementation strategy: an <see cref="AgentPackage"/> is structurally a
/// <see cref="WorkflowPackage"/> with an empty <see cref="WorkflowPackage.Workflows"/>
/// collection. We synthesize the latter shape from the former and delegate to a
/// <see cref="WorkflowPackageImporter"/> instance configured with
/// <see cref="AgentPackageImportValidator"/> as its admission validator. Every workflow
/// path inside the importer (subflow planning, port-shape refusal, workflow-row insert,
/// CollectValidationErrors over workflow shapes) is a no-op when the workflow collection
/// is empty, so reuse is safe; the entry-point rewrite path consults
/// <c>agentRewrites</c> in addition to <c>workflowRewrites</c> so Bump / Copy / UseExisting
/// on the entry-point agent rewrites the EntryPoint reference correctly.
/// <para/>
/// Reuses every preview / apply DTO type from the workflow side — both sets of rows render
/// through the same imports-page UI in AP-7.
/// </summary>
public sealed class AgentPackageImporter(
    CodeFlowDbContext dbContext,
    IWorkflowRepository workflowRepository,
    IAgentConfigRepository agentConfigRepository,
    IAgentRoleRepository agentRoleRepository,
    ISkillRepository skillRepository,
    IMcpServerRepository mcpServerRepository,
    IMcpEndpointPolicy mcpEndpointPolicy,
    WorkflowValidationPipeline? validationPipeline = null,
    IAuthoringTelemetry? telemetry = null) : IAgentPackageImporter
{
    private static readonly AgentPackageImportValidator AgentAdmissionValidator = new();

    public Task<WorkflowPackageImportPreview> PreviewAsync(
        AgentPackage package,
        CancellationToken cancellationToken = default) =>
        PreviewAsync(package, resolutions: null, cancellationToken);

    public Task<WorkflowPackageImportPreview> PreviewAsync(
        AgentPackage package,
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        return CreateInner().PreviewAsync(SynthesizeWorkflowPackage(package), resolutions, cancellationToken);
    }

    public Task<WorkflowPackageImportApplyResult> ApplyAsync(
        AgentPackage package,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(package, resolutions: null, cancellationToken);

    public Task<WorkflowPackageImportApplyResult> ApplyAsync(
        AgentPackage package,
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        return CreateInner().ApplyAsync(SynthesizeWorkflowPackage(package), resolutions, cancellationToken);
    }

    private WorkflowPackageImporter CreateInner() => new(
        dbContext,
        workflowRepository,
        agentConfigRepository,
        agentRoleRepository,
        skillRepository,
        mcpServerRepository,
        mcpEndpointPolicy,
        validationPipeline,
        telemetry,
        AgentAdmissionValidator);

    /// <summary>
    /// Lift an <see cref="AgentPackage"/> into the <see cref="WorkflowPackage"/> shape the
    /// inner importer consumes. Schema string is preserved as <c>codeflow.agent-package.v1</c>
    /// so the agent admission validator's schema check matches; an empty
    /// <see cref="WorkflowPackage.Workflows"/> makes every workflow code-path a no-op.
    /// </summary>
    private static WorkflowPackage SynthesizeWorkflowPackage(AgentPackage agentPackage) => new(
        SchemaVersion: agentPackage.SchemaVersion,
        Metadata: agentPackage.Metadata,
        EntryPoint: agentPackage.EntryPoint,
        Workflows: Array.Empty<WorkflowPackageWorkflow>(),
        Agents: agentPackage.Agents,
        AgentRoleAssignments: agentPackage.AgentRoleAssignments,
        Roles: agentPackage.Roles,
        Skills: agentPackage.Skills,
        McpServers: agentPackage.McpServers,
        Manifest: agentPackage.Manifest is null
            ? null
            : new WorkflowPackageManifest(
                Workflows: Array.Empty<WorkflowPackageReference>(),
                Agents: new[] { agentPackage.Manifest.Agent },
                Roles: agentPackage.Manifest.Roles,
                Skills: agentPackage.Manifest.Skills,
                McpServers: agentPackage.Manifest.McpServers));
}
