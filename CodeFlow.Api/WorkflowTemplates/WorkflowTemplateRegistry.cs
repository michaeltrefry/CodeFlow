namespace CodeFlow.Api.WorkflowTemplates;

/// <summary>
/// In-memory catalog of <see cref="WorkflowTemplate"/> blueprints shipped with the platform.
/// S3 ships only the <see cref="EmptyWorkflowId"/> stub — proves the materialization path
/// end-to-end. S4 (ReviewLoop pair), S5 (HITL gate), S6 (Setup → loop → finalize), and S7
/// (Lifecycle wrapper) each register additional templates here.
///
/// Lookup is case-insensitive on the template id so the URL routing layer doesn't need to
/// special-case casing.
/// </summary>
public sealed class WorkflowTemplateRegistry
{
    public const string EmptyWorkflowId = "empty-workflow";

    private readonly IReadOnlyDictionary<string, WorkflowTemplate> templates;

    public WorkflowTemplateRegistry()
        : this(EmptyWorkflowTemplate.Build())
    {
    }

    public WorkflowTemplateRegistry(params WorkflowTemplate[] templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        this.templates = templates.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<WorkflowTemplate> List() => templates.Values
        .OrderBy(t => t.Category)
        .ThenBy(t => t.Name, StringComparer.Ordinal)
        .ToArray();

    public WorkflowTemplate? GetOrDefault(string id) =>
        string.IsNullOrWhiteSpace(id) ? null
        : templates.TryGetValue(id, out var template) ? template : null;
}
