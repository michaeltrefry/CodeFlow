namespace CodeFlow.Api.WorkflowPackages;

/// <summary>
/// AP-2 (sc-833): import side of the agent-package round trip. Mirrors the public surface
/// of <see cref="IWorkflowPackageImporter"/> at the agent level — preview a package, then
/// apply it (optionally with per-row resolutions). Reuses every preview / apply DTO type
/// so the imports UI can render agent and workflow rows with the same components.
/// </summary>
public interface IAgentPackageImporter
{
    Task<WorkflowPackageImportPreview> PreviewAsync(
        AgentPackage package,
        CancellationToken cancellationToken = default);

    Task<WorkflowPackageImportPreview> PreviewAsync(
        AgentPackage package,
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
        CancellationToken cancellationToken = default);

    Task<WorkflowPackageImportApplyResult> ApplyAsync(
        AgentPackage package,
        CancellationToken cancellationToken = default);

    Task<WorkflowPackageImportApplyResult> ApplyAsync(
        AgentPackage package,
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
        CancellationToken cancellationToken = default);
}
