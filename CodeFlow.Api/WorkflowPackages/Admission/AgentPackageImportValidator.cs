using CodeFlow.Runtime.Authority.Admission;

namespace CodeFlow.Api.WorkflowPackages.Admission;

/// <summary>
/// Mints <see cref="AdmittedPackageImport"/> values from the <see cref="WorkflowPackage"/>
/// shape produced by <see cref="AgentPackageImporter"/>'s synthesize step (one entry-point
/// agent + role / skill / MCP-server closure, no workflows). Performs structural checks
/// only — schema version match and entry-point presence in the agents collection. Refs
/// inside the agent's role/skill/MCP closure that aren't embedded in the package fall
/// through to the importer's planner the same way they do for workflow packages.
///
/// Refusal taxonomy:
/// <list type="bullet">
///   <item><description><c>package-schema-unsupported</c> — schema version doesn't match
///     <see cref="AgentPackageDefaults.SchemaVersion"/>.</description></item>
///   <item><description><c>package-entry-point-missing</c> — entry point reference isn't
///     included in the agents collection.</description></item>
/// </list>
/// </summary>
public sealed class AgentPackageImportValidator : IAdmissionValidator<WorkflowPackage, AdmittedPackageImport>
{
    private readonly Func<DateTimeOffset> nowProvider;

    public AgentPackageImportValidator(Func<DateTimeOffset>? nowProvider = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public Admission<AdmittedPackageImport> Validate(WorkflowPackage input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!string.Equals(input.SchemaVersion, AgentPackageDefaults.SchemaVersion, StringComparison.Ordinal))
        {
            return Admission<AdmittedPackageImport>.Reject(new Rejection(
                Code: "package-schema-unsupported",
                Reason: $"Agent package schema '{input.SchemaVersion}' is not supported.",
                Axis: "package-import",
                Path: input.SchemaVersion));
        }

        if (!input.Agents.Any(agent =>
                string.Equals(agent.Key, input.EntryPoint.Key, StringComparison.Ordinal) &&
                agent.Version == input.EntryPoint.Version))
        {
            return Admission<AdmittedPackageImport>.Reject(new Rejection(
                Code: "package-entry-point-missing",
                Reason: $"Agent package entry point '{input.EntryPoint.Key}' v{input.EntryPoint.Version} is missing from agents.",
                Axis: "package-import",
                Path: $"{input.EntryPoint.Key}@{input.EntryPoint.Version}"));
        }

        return Admission<AdmittedPackageImport>.Accept(new AdmittedPackageImport(input, nowProvider()));
    }
}
