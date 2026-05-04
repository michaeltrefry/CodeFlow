namespace CodeFlow.Api.WorkflowPackages;

public interface IWorkflowPackageImporter
{
    Task<WorkflowPackageImportPreview> PreviewAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// sc-394: preview the import after applying user-chosen per-conflict resolutions
    /// (UseExisting / Bump / Copy). The resolutions transform the package (drop entities,
    /// rewrite versions, rename keys) and rewrite every transitive workflow-node ref before
    /// the planner runs — so the returned preview reflects the post-resolution shape.
    /// </summary>
    Task<WorkflowPackageImportPreview> PreviewAsync(
        WorkflowPackage package,
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
        CancellationToken cancellationToken = default);

    Task<WorkflowPackageImportApplyResult> ApplyAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// sc-394: apply the import after user-chosen resolutions. <c>Bump</c> and <c>Copy</c>
    /// outcomes write <see cref="AgentConfigEntity.ForkedFromKey"/> /
    /// <see cref="AgentConfigEntity.ForkedFromVersion"/> lineage on the new agent rows so the
    /// (originalKey, originalVersion) → (resolvedKey, resolvedVersion) provenance stays
    /// queryable. Workflows have no lineage columns yet; their resolution outcomes still
    /// rewrite refs but don't carry lineage forward.
    /// </summary>
    Task<WorkflowPackageImportApplyResult> ApplyAsync(
        WorkflowPackage package,
        IReadOnlyDictionary<WorkflowPackageImportResolutionKey, WorkflowPackageImportResolution>? resolutions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the same workflow validators ApplyAsync runs, but inside a rollback-only transaction
    /// so the package is never persisted. Mirrors the editor's save-time validation surface for
    /// authoring flows (the homepage assistant's <c>save_workflow_package</c> tool) that need to
    /// know whether the package would actually land — without committing it.
    /// <para/>
    /// Returns <see cref="WorkflowPackageValidationResult.Valid"/> when every embedded workflow
    /// passes the legacy validator and the validation pipeline. Otherwise returns the per-workflow
    /// error list. Conflict-only packages (preview cannot apply) short-circuit to Valid — fix the
    /// conflicts first; we'll re-validate on the corrected package.
    /// </summary>
    Task<WorkflowPackageValidationResult> ValidateAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Identity of a single Conflict row the user is resolving, keyed by resource kind, identity
/// (entity Key — Name for Skill, AgentKey for AgentRoleAssignment, Key for the rest), and the
/// package's source version. <see cref="SourceVersion"/> is non-null for versioned kinds
/// (Workflow, Agent) and null for unversioned kinds (Role, Skill, McpServer, AgentRoleAssignment).
/// Mirrors <see cref="WorkflowPackageImportItem.SourceVersion"/> on the preview side.
/// </summary>
public sealed record WorkflowPackageImportResolutionKey(
    WorkflowPackageImportResourceKind Kind,
    string Key,
    int? SourceVersion);

/// <summary>
/// Per-Conflict resolution chosen by the user. The importer applies it before planning so the
/// resulting preview / apply describe the post-resolution shape.
/// <list type="bullet">
///   <item><description><see cref="WorkflowPackageImportResolutionMode.UseExisting"/> — drop the
///     package's lower version; rewrite every workflow node that pinned this entity to point at
///     the library's higher version. Valid for any kind.</description></item>
///   <item><description><see cref="WorkflowPackageImportResolutionMode.Bump"/> — set the entity's
///     version to <c>existingMaxVersion + 1</c>; rewrite transitive node refs to the bumped
///     version. Valid only for versioned kinds (Agent, Workflow). New agent row carries
///     ForkedFromKey/Version lineage to the original (key, sourceVersion).</description></item>
///   <item><description><see cref="WorkflowPackageImportResolutionMode.Copy"/> — rename the entity
///     to <see cref="NewKey"/> at version 1; rewrite transitive node refs and (for agents) role
///     assignments to the new key. Valid only for versioned kinds. Sets agent lineage to the
///     original (key, sourceVersion).</description></item>
/// </list>
/// </summary>
/// <param name="Target">The Conflict row this resolution targets.</param>
/// <param name="Mode">Which of the three deterministic resolutions to apply.</param>
/// <param name="NewKey">Required when <paramref name="Mode"/> is
/// <see cref="WorkflowPackageImportResolutionMode.Copy"/>; null otherwise.</param>
public sealed record WorkflowPackageImportResolution(
    WorkflowPackageImportResolutionKey Target,
    WorkflowPackageImportResolutionMode Mode,
    string? NewKey = null);

public enum WorkflowPackageImportResolutionMode
{
    UseExisting,
    Bump,
    Copy,
}

public sealed record WorkflowPackageValidationResult(
    bool IsValid,
    IReadOnlyList<WorkflowPackageValidationError> Errors)
{
    public static readonly WorkflowPackageValidationResult Valid =
        new(true, Array.Empty<WorkflowPackageValidationError>());
}

public sealed record WorkflowPackageValidationError(
    string WorkflowKey,
    string Message,
    IReadOnlyList<string>? RuleIds = null);

public sealed record WorkflowPackageImportPreview(
    WorkflowPackageReference EntryPoint,
    IReadOnlyList<WorkflowPackageImportItem> Items,
    IReadOnlyList<string> Warnings)
{
    public int CreateCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Create);

    public int ReuseCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Reuse);

    public int ConflictCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Conflict);

    /// <summary>sc-395: rows that can't be applied due to a structural refusal (e.g., a
    /// UseExisting resolution whose target library version doesn't declare every output port
    /// the package's nodes route to). Treated as a hard apply-blocker just like Conflict.</summary>
    public int RefusedCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Refused);

    public int WarningCount => Warnings.Count;

    public bool CanApply => ConflictCount == 0 && RefusedCount == 0;
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

    public int RefusedCount => Items.Count(item => item.Action == WorkflowPackageImportAction.Refused);

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

    /// <summary>sc-395: a user-supplied resolution can't be applied for a structural reason —
    /// today only "UseExisting on an agent whose library max-version doesn't declare every
    /// output port the package's nodes route to." Behaves like Conflict for apply-gating
    /// purposes but is distinct so the imports-page UI can render it differently and the
    /// chat-side chip can suggest re-resolving.</summary>
    Refused,
}
