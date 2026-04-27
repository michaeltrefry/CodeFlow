namespace CodeFlow.Api.Validation.Pipeline;

/// <summary>
/// Severity of a finding emitted by a workflow validation rule.
/// <see cref="Error"/> blocks save; <see cref="Warning"/> allows save but surfaces in the
/// editor's results panel.
/// </summary>
public enum WorkflowValidationSeverity
{
    Warning = 0,
    Error = 1,
}

/// <summary>
/// Where a validation finding lives in the workflow graph. All fields optional so rules can emit
/// a finding without a specific anchor (e.g. a workflow-level structural error). The editor uses
/// these to highlight + click-to-jump.
/// </summary>
/// <param name="NodeId">Id of the offending node, if any.</param>
/// <param name="EdgeFrom">Source node id of the offending edge, if any. Pair with <see cref="EdgePort"/>.</param>
/// <param name="EdgePort">Source port name of the offending edge, if any.</param>
public sealed record WorkflowValidationLocation(
    Guid? NodeId = null,
    Guid? EdgeFrom = null,
    string? EdgePort = null);

/// <summary>
/// A single finding produced by a <see cref="IWorkflowValidationRule"/>. The pipeline aggregates
/// findings across all rules; the editor groups them by severity and rule id for display.
/// </summary>
/// <param name="RuleId">Stable identifier for the rule that produced this finding (e.g.
/// <c>port-coupling</c>). Used by the editor to group findings and by telemetry to aggregate.</param>
/// <param name="Severity">Severity tier — drives whether this finding blocks save.</param>
/// <param name="Message">Human-readable explanation. Single sentence preferred; rules should
/// keep messages free of internal jargon.</param>
/// <param name="Location">Optional anchor for click-to-jump in the editor.</param>
public sealed record WorkflowValidationFinding(
    string RuleId,
    WorkflowValidationSeverity Severity,
    string Message,
    WorkflowValidationLocation? Location = null);
