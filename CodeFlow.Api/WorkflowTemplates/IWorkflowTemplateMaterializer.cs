namespace CodeFlow.Api.WorkflowTemplates;

public interface IWorkflowTemplateMaterializer
{
    Task<MaterializedTemplateResult> MaterializeAsync(
        string templateId,
        string namePrefix,
        string? createdBy,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowTemplateNotFoundException : Exception
{
    public WorkflowTemplateNotFoundException(string templateId)
        : base($"No workflow template registered with id '{templateId}'.")
    {
        TemplateId = templateId;
    }

    public string TemplateId { get; }
}

/// <summary>
/// Thrown when a template's planned entity keys would collide with already-existing rows.
/// The materializer raises this BEFORE writing anything so partial materialization never
/// leaves orphan agents on a failed run.
/// </summary>
public sealed class TemplateKeyCollisionException : Exception
{
    public TemplateKeyCollisionException(IReadOnlyList<MaterializedEntity> conflicts)
        : base(BuildMessage(conflicts))
    {
        Conflicts = conflicts;
    }

    public IReadOnlyList<MaterializedEntity> Conflicts { get; }

    private static string BuildMessage(IReadOnlyList<MaterializedEntity> conflicts)
    {
        var rendered = string.Join(
            ", ",
            conflicts.Select(c => $"{c.Kind.ToString().ToLowerInvariant()} '{c.Key}'"));
        return $"Cannot materialize template — the following keys already exist: {rendered}.";
    }
}
