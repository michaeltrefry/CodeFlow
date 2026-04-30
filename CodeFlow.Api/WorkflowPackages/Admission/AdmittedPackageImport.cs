using CodeFlow.Runtime.Authority.Admission;

namespace CodeFlow.Api.WorkflowPackages.Admission;

/// <summary>
/// Witness that a <see cref="WorkflowPackage"/> passed dependency-closure admission: the
/// schema version is supported, the entry point is in the workflow set, and every agent /
/// subflow reference inside the included workflows resolves to a sibling included in the
/// same package. Produced only by <see cref="WorkflowPackageImportValidator"/>; the
/// importer's <c>BuildImportPlanAsync</c> consumes this type rather than the raw package
/// so the no-validation path is gone at compile time.
///
/// Re-mint discipline: persistence stores the source <see cref="WorkflowPackage"/>;
/// re-validating it through the validator on a fresh process produces an equivalent
/// admitted value (modulo wall-clock fields).
/// </summary>
public sealed class AdmittedPackageImport
{
    /// <summary>Validator-only constructor.</summary>
    internal AdmittedPackageImport(WorkflowPackage package, DateTimeOffset admittedAt)
    {
        Package = package;
        AdmittedAt = admittedAt;
    }

    /// <summary>The original package the validator was handed. Re-mint replays through the validator.</summary>
    public WorkflowPackage Package { get; }

    /// <summary>UTC instant the validator minted this admission.</summary>
    public DateTimeOffset AdmittedAt { get; }
}
