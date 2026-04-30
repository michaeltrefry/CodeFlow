namespace CodeFlow.Api.WorkflowPackages;

public interface IWorkflowPackageImporter
{
    Task<WorkflowPackageImportPreview> PreviewAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default);

    Task<WorkflowPackageImportApplyResult> ApplyAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowPackageImportPreview(
    WorkflowPackageReference EntryPoint,
    IReadOnlyList<WorkflowPackageImportItem> Items,
    IReadOnlyList<string> Warnings)
{
    public int CreateCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Create);

    public int ReuseCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Reuse);

    public int ConflictCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Conflict);

    public int WarningCount => Warnings.Count;

    public bool CanApply => ConflictCount == 0;
}

/// <summary>
/// One row in the import preview's per-resource plan. <see cref="Version"/> is the post-rewrite
/// target version (what the import would create, reuse, or fail at) — for <see cref="WorkflowPackageImportAction.Create"/>
/// rows produced by the version-bump path, that's the bumped target, not the package's source version.
/// <para/>
/// <see cref="SourceVersion"/> and <see cref="ExistingMaxVersion"/> are sc-393's per-conflict
/// resolution affordance: a client can compute Bump / UseExisting / Copy choices without parsing
/// the human-readable <see cref="Message"/> string. Populated only where meaningful (versioned
/// kinds: Workflow, Agent). Non-versioned kinds (Role, Skill, McpServer, AgentRoleAssignment)
/// leave both null.
/// </summary>
public sealed record WorkflowPackageImportItem(
    WorkflowPackageImportResourceKind Kind,
    string Key,
    int? Version,
    WorkflowPackageImportAction Action,
    string Message,
    /// <summary>
    /// The version the package itself carries for this entity, before any importer rewrite.
    /// For Create / Reuse rows this equals <see cref="Version"/>; for the auto-bump Create rows
    /// produced when content differs at a same-numbered library version, this is the original
    /// package version while <see cref="Version"/> is the bumped target.
    /// </summary>
    int? SourceVersion = null,
    /// <summary>
    /// The highest version present in the local library for this <see cref="Key"/>, or null when
    /// no version exists yet (or for non-versioned kinds). For a Conflict caused by a stale
    /// package version, this is the value the package would need to bump *above* to land cleanly.
    /// </summary>
    int? ExistingMaxVersion = null);

public sealed record WorkflowPackageImportApplyResult(
    WorkflowPackageReference EntryPoint,
    IReadOnlyList<WorkflowPackageImportItem> Items,
    IReadOnlyList<string> Warnings)
{
    public int CreateCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Create);

    public int ReuseCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Reuse);

    public int ConflictCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Conflict);

    public int WarningCount => Warnings.Count;
}

public enum WorkflowPackageImportResourceKind
{
    Workflow,
    Agent,
    AgentRoleAssignment,
    Role,
    Skill,
    McpServer,
}

public enum WorkflowPackageImportAction
{
    Create,
    Reuse,
    Conflict,
}
