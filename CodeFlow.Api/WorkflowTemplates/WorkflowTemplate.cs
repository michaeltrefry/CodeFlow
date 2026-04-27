using CodeFlow.Persistence;

namespace CodeFlow.Api.WorkflowTemplates;

/// <summary>
/// S3 (Workflow Authoring DX): blueprint for materializing a coordinated set of entities
/// (agents + workflows + role assignments) when an author picks "New from template" in the
/// editor.
///
/// Templates are static-shipped today via <see cref="WorkflowTemplateRegistry"/>. The framework
/// itself imposes no naming convention on the materialized keys — each template's materializer
/// composes the operator-supplied <see cref="TemplateMaterializationContext.NamePrefix"/> into
/// concrete agent / workflow keys however it sees fit. Collisions surface as 409 Conflict from
/// the underlying repository when the materializer attempts to create an entity at an
/// already-taken key.
/// </summary>
public sealed record WorkflowTemplate(
    string Id,
    string Name,
    string Description,
    WorkflowTemplateCategory Category,
    Func<TemplateMaterializationContext, Task<MaterializedTemplateResult>> Materialize,
    Func<string, IReadOnlyList<PlannedEntityKey>> PlanKeys);

/// <summary>
/// Names an entity a template intends to create. The materializer does a pre-flight
/// existence check across all planned keys before the template's <c>Materialize</c> delegate
/// runs, so a collision is reported atomically as
/// <see cref="TemplateKeyCollisionException"/> instead of leaving orphan rows.
/// </summary>
public sealed record PlannedEntityKey(MaterializedEntityKind Kind, string Key);

public enum WorkflowTemplateCategory
{
    Empty,
    ReviewLoop,
    Hitl,
    Lifecycle,
    Other,
}

public sealed record TemplateMaterializationContext(
    string NamePrefix,
    string? CreatedBy,
    IAgentConfigRepository AgentRepository,
    IWorkflowRepository WorkflowRepository,
    IAgentRoleRepository RoleRepository,
    CancellationToken CancellationToken);

public sealed record MaterializedTemplateResult(
    string EntryWorkflowKey,
    int EntryWorkflowVersion,
    IReadOnlyList<MaterializedEntity> CreatedEntities);

public sealed record MaterializedEntity(
    MaterializedEntityKind Kind,
    string Key,
    int Version);

public enum MaterializedEntityKind
{
    Workflow,
    Agent,
}
