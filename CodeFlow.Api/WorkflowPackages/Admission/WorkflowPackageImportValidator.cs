using CodeFlow.Runtime.Authority.Admission;

namespace CodeFlow.Api.WorkflowPackages.Admission;

/// <summary>
/// Mints <see cref="AdmittedPackageImport"/> values from raw <see cref="WorkflowPackage"/>
/// inputs. Verifies the package's dependency closure up-front so the importer can no
/// longer accept a partial package and silently land workflows that reference missing
/// agents or subflows. Replaces the existing static <c>ValidatePackage</c> throw path
/// inside <see cref="WorkflowPackageImporter"/>.
///
/// Refusal taxonomy (rejection codes):
/// <list type="bullet">
///   <item><description><c>package-schema-unsupported</c> — schema version doesn't match the importer's expected version.</description></item>
///   <item><description><c>package-entry-point-missing</c> — entry point reference isn't included in the workflow set.</description></item>
///   <item><description><c>package-node-missing-agent-version</c> — an agent node has no concrete agent version pin.</description></item>
///   <item><description><c>package-agent-missing</c> — an agent node references an agent (key, version) that isn't included in the package.</description></item>
///   <item><description><c>package-node-missing-subflow-version</c> — a subflow node has no concrete subflow version pin.</description></item>
///   <item><description><c>package-subflow-missing</c> — a subflow node references a workflow (key, version) that isn't included in the package.</description></item>
/// </list>
/// </summary>
public sealed class WorkflowPackageImportValidator : IAdmissionValidator<WorkflowPackage, AdmittedPackageImport>
{
    private readonly Func<DateTimeOffset> nowProvider;

    public WorkflowPackageImportValidator(Func<DateTimeOffset>? nowProvider = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public Admission<AdmittedPackageImport> Validate(WorkflowPackage input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!string.Equals(input.SchemaVersion, WorkflowPackageDefaults.SchemaVersion, StringComparison.Ordinal))
        {
            return Admission<AdmittedPackageImport>.Reject(new Rejection(
                Code: "package-schema-unsupported",
                Reason: $"Workflow package schema '{input.SchemaVersion}' is not supported.",
                Axis: "package-import",
                Path: input.SchemaVersion));
        }

        if (!input.Workflows.Any(workflow =>
                string.Equals(workflow.Key, input.EntryPoint.Key, StringComparison.Ordinal) &&
                workflow.Version == input.EntryPoint.Version))
        {
            return Admission<AdmittedPackageImport>.Reject(new Rejection(
                Code: "package-entry-point-missing",
                Reason: $"Workflow package entry point '{input.EntryPoint.Key}' v{input.EntryPoint.Version} is missing from workflows.",
                Axis: "package-import",
                Path: $"{input.EntryPoint.Key}@{input.EntryPoint.Version}"));
        }

        var workflowKeys = input.Workflows
            .Select(workflow => (workflow.Key, workflow.Version))
            .ToHashSet();
        var agentKeys = input.Agents
            .Select(agent => (agent.Key, agent.Version))
            .ToHashSet();

        foreach (var workflow in input.Workflows)
        {
            foreach (var node in workflow.Nodes)
            {
                if (CheckAgentRef(workflow, node) is { } agentRejection) return Admission<AdmittedPackageImport>.Reject(agentRejection);
                if (CheckSubflowRef(workflow, node) is { } subflowRejection) return Admission<AdmittedPackageImport>.Reject(subflowRejection);
            }
        }

        return Admission<AdmittedPackageImport>.Accept(new AdmittedPackageImport(input, nowProvider()));

        Rejection? CheckAgentRef(WorkflowPackageWorkflow workflow, WorkflowPackageWorkflowNode node)
        {
            if (string.IsNullOrWhiteSpace(node.AgentKey))
            {
                return null;
            }

            if (node.AgentVersion is null)
            {
                return new Rejection(
                    Code: "package-node-missing-agent-version",
                    Reason: $"Workflow '{workflow.Key}' v{workflow.Version} has agent node '{node.Id}' without a concrete agent version.",
                    Axis: "package-import",
                    Path: $"{workflow.Key}@{workflow.Version}/{node.Id}");
            }

            if (!agentKeys.Contains((node.AgentKey!, node.AgentVersion.Value)))
            {
                return new Rejection(
                    Code: "package-agent-missing",
                    Reason: $"Workflow '{workflow.Key}' v{workflow.Version} references missing agent '{node.AgentKey}' v{node.AgentVersion}.",
                    Axis: "package-import",
                    Path: $"{node.AgentKey}@{node.AgentVersion}");
            }

            return null;
        }

        Rejection? CheckSubflowRef(WorkflowPackageWorkflow workflow, WorkflowPackageWorkflowNode node)
        {
            if (string.IsNullOrWhiteSpace(node.SubflowKey))
            {
                return null;
            }

            if (node.SubflowVersion is null)
            {
                return new Rejection(
                    Code: "package-node-missing-subflow-version",
                    Reason: $"Workflow '{workflow.Key}' v{workflow.Version} has subflow node '{node.Id}' without a concrete subflow version.",
                    Axis: "package-import",
                    Path: $"{workflow.Key}@{workflow.Version}/{node.Id}");
            }

            if (!workflowKeys.Contains((node.SubflowKey!, node.SubflowVersion.Value)))
            {
                return new Rejection(
                    Code: "package-subflow-missing",
                    Reason: $"Workflow '{workflow.Key}' v{workflow.Version} references missing subflow '{node.SubflowKey}' v{node.SubflowVersion}.",
                    Axis: "package-import",
                    Path: $"{node.SubflowKey}@{node.SubflowVersion}");
            }

            return null;
        }
    }
}
