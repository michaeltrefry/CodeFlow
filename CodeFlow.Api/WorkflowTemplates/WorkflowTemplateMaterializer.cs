using CodeFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.WorkflowTemplates;

/// <summary>
/// Default <see cref="IWorkflowTemplateMaterializer"/>: looks up the template, validates the
/// name prefix, builds a <see cref="TemplateMaterializationContext"/> from request-scoped
/// repositories, and dispatches to the template's own materialize delegate.
///
/// Errors surface naturally:
/// <list type="bullet">
///   <item><description>Unknown template id → <see cref="WorkflowTemplateNotFoundException"/>.</description></item>
///   <item><description>Invalid prefix (empty/whitespace, illegal characters) →
///   <see cref="ArgumentException"/>.</description></item>
///   <item><description>Repository-level conflicts (key already exists) propagate from the
///   underlying CreateNewVersionAsync calls — the endpoint surfaces them as 409.</description></item>
/// </list>
/// </summary>
public sealed class WorkflowTemplateMaterializer : IWorkflowTemplateMaterializer
{
    private readonly WorkflowTemplateRegistry registry;
    private readonly IAgentConfigRepository agentRepository;
    private readonly IWorkflowRepository workflowRepository;
    private readonly IAgentRoleRepository roleRepository;
    private readonly CodeFlowDbContext dbContext;

    public WorkflowTemplateMaterializer(
        WorkflowTemplateRegistry registry,
        IAgentConfigRepository agentRepository,
        IWorkflowRepository workflowRepository,
        IAgentRoleRepository roleRepository,
        CodeFlowDbContext dbContext)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.agentRepository = agentRepository ?? throw new ArgumentNullException(nameof(agentRepository));
        this.workflowRepository = workflowRepository ?? throw new ArgumentNullException(nameof(workflowRepository));
        this.roleRepository = roleRepository ?? throw new ArgumentNullException(nameof(roleRepository));
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<MaterializedTemplateResult> MaterializeAsync(
        string templateId,
        string namePrefix,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        var template = registry.GetOrDefault(templateId)
            ?? throw new WorkflowTemplateNotFoundException(templateId);

        var prefix = NormalizePrefix(namePrefix);

        await EnsureKeysAvailableAsync(template, prefix, cancellationToken);

        var context = new TemplateMaterializationContext(
            NamePrefix: prefix,
            CreatedBy: string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            AgentRepository: agentRepository,
            WorkflowRepository: workflowRepository,
            RoleRepository: roleRepository,
            CancellationToken: cancellationToken);

        return await template.Materialize(context);
    }

    private async Task EnsureKeysAvailableAsync(
        WorkflowTemplate template,
        string prefix,
        CancellationToken cancellationToken)
    {
        var planned = template.PlanKeys(prefix);
        if (planned.Count == 0)
        {
            return;
        }

        var agentKeys = planned
            .Where(p => p.Kind == MaterializedEntityKind.Agent)
            .Select(p => p.Key)
            .ToArray();
        var workflowKeys = planned
            .Where(p => p.Kind == MaterializedEntityKind.Workflow)
            .Select(p => p.Key)
            .ToArray();

        var existingAgents = agentKeys.Length == 0
            ? Array.Empty<string>()
            : await dbContext.Agents
                .AsNoTracking()
                .Where(a => agentKeys.Contains(a.Key))
                .Select(a => a.Key)
                .Distinct()
                .ToArrayAsync(cancellationToken);

        var existingWorkflows = workflowKeys.Length == 0
            ? Array.Empty<string>()
            : await dbContext.Workflows
                .AsNoTracking()
                .Where(w => workflowKeys.Contains(w.Key))
                .Select(w => w.Key)
                .Distinct()
                .ToArrayAsync(cancellationToken);

        var conflicts = new List<MaterializedEntity>();
        conflicts.AddRange(existingAgents
            .Select(k => new MaterializedEntity(MaterializedEntityKind.Agent, k, 0)));
        conflicts.AddRange(existingWorkflows
            .Select(k => new MaterializedEntity(MaterializedEntityKind.Workflow, k, 0)));

        if (conflicts.Count > 0)
        {
            throw new TemplateKeyCollisionException(conflicts);
        }
    }

    private static string NormalizePrefix(string namePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namePrefix);
        var trimmed = namePrefix.Trim();
        if (!IsValidPrefix(trimmed))
        {
            throw new ArgumentException(
                $"Name prefix '{trimmed}' contains invalid characters. Use only letters, "
                + "digits, hyphens, and underscores.",
                nameof(namePrefix));
        }
        return trimmed;
    }

    private static bool IsValidPrefix(string prefix)
    {
        foreach (var ch in prefix)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))
            {
                return false;
            }
        }
        return true;
    }
}
