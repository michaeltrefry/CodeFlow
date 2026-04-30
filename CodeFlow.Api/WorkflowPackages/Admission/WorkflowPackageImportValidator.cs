using CodeFlow.Runtime.Authority.Admission;

namespace CodeFlow.Api.WorkflowPackages.Admission;

/// <summary>
/// Mints <see cref="AdmittedPackageImport"/> values from raw <see cref="WorkflowPackage"/>
/// inputs. Performs structural checks only — schema version, entry-point presence in the
/// workflow set, and concrete version pins on agent/subflow refs. Closure of unembedded
/// agent/subflow references is resolved by the importer's planner against the local
/// database (a node may reference an agent or subflow that already exists in the target
/// library without re-embedding it). The exporter still produces fully self-contained
/// bundles for portability; this relaxation is an import-side affordance.
///
/// Refusal taxonomy (rejection codes):
/// <list type="bullet">
///   <item><description><c>package-schema-unsupported</c> — schema version doesn't match the importer's expected version.</description></item>
///   <item><description><c>package-entry-point-missing</c> — entry point reference isn't included in the workflow set.</description></item>
///   <item><description><c>package-node-missing-agent-version</c> — an agent node has no concrete agent version pin.</description></item>
///   <item><description><c>package-node-missing-subflow-version</c> — a subflow node has no concrete subflow version pin.</description></item>
/// </list>
///
/// Refs that point at a (key, version) not embedded in the package are NOT rejected here.
/// The planner emits a per-resource <c>Reuse</c> item if the entity exists in the target
/// DB or a <c>Conflict</c> item if it doesn't, surfacing the issue in the import preview
/// instead of as a top-level rejection.
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

        foreach (var workflow in input.Workflows)
        {
            foreach (var node in workflow.Nodes)
            {
                if (CheckAgentVersionPin(workflow, node) is { } agentRejection) return Admission<AdmittedPackageImport>.Reject(agentRejection);
                if (CheckSubflowVersionPin(workflow, node) is { } subflowRejection) return Admission<AdmittedPackageImport>.Reject(subflowRejection);
            }
        }

        return Admission<AdmittedPackageImport>.Accept(new AdmittedPackageImport(input, nowProvider()));

        static Rejection? CheckAgentVersionPin(WorkflowPackageWorkflow workflow, WorkflowPackageWorkflowNode node)
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

            return null;
        }

        static Rejection? CheckSubflowVersionPin(WorkflowPackageWorkflow workflow, WorkflowPackageWorkflowNode node)
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

            return null;
        }
    }
}
