using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;

namespace CodeFlow.Api.Validation.Pipeline.Rules;

/// <summary>
/// Cheap structural advisory: warn when no Start node has an input script. Not blocking — many
/// workflows legitimately route the user-supplied artifact straight into the start agent without
/// preprocessing — but visible so authors who *intended* to seed workflow variables at trace
/// launch (the common case for code-aware workflows) notice the omission.
///
/// Acts as the placeholder rule for F1 to verify pipeline wiring end-to-end. Real rules
/// (port-coupling, missing-role, backedge, prompt-lint, package-self-containment) land in V4-V8
/// and supersede this one's role as the "is the pipeline alive" canary.
/// </summary>
public sealed class StartNodeAdvisoryRule : IWorkflowValidationRule
{
    public string RuleId => "start-node-input-script-advisory";
    public int Order => 50;

    public Task<IReadOnlyList<WorkflowValidationFinding>> RunAsync(
        WorkflowValidationContext context,
        CancellationToken cancellationToken)
    {
        var startNode = context.Nodes.FirstOrDefault(IsStart);
        if (startNode is null || !string.IsNullOrWhiteSpace(startNode.InputScript))
        {
            return EmptyTask;
        }

        var finding = new WorkflowValidationFinding(
            RuleId: RuleId,
            Severity: WorkflowValidationSeverity.Warning,
            Message: "Start node has no input script. Workflows that need to seed workflow "
                + "variables at trace launch (e.g. repositories or a task-specific prdTitle) "
                + "typically do so from the Start node's input script. Skip this warning if "
                + "your start agent reads the raw input artifact directly.",
            Location: new WorkflowValidationLocation(NodeId: startNode.Id));

        return Task.FromResult<IReadOnlyList<WorkflowValidationFinding>>(new[] { finding });
    }

    private static bool IsStart(WorkflowNodeDto node) => node.Kind == WorkflowNodeKind.Start;

    private static readonly Task<IReadOnlyList<WorkflowValidationFinding>> EmptyTask =
        Task.FromResult<IReadOnlyList<WorkflowValidationFinding>>(Array.Empty<WorkflowValidationFinding>());
}
