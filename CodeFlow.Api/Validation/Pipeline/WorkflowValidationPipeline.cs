using Microsoft.Extensions.Logging;

namespace CodeFlow.Api.Validation.Pipeline;

/// <summary>
/// Result of running the validation pipeline against a workflow draft.
/// </summary>
/// <param name="Findings">All findings emitted by every rule that ran, ordered by severity
/// (Errors first) then by registration order.</param>
public sealed record WorkflowValidationReport(
    IReadOnlyList<WorkflowValidationFinding> Findings)
{
    public bool HasErrors => Findings.Any(f => f.Severity == WorkflowValidationSeverity.Error);
    public bool HasWarnings => Findings.Any(f => f.Severity == WorkflowValidationSeverity.Warning);

    public static readonly WorkflowValidationReport Empty = new(Array.Empty<WorkflowValidationFinding>());
}

/// <summary>
/// Runs every registered <see cref="IWorkflowValidationRule"/> against a
/// <see cref="WorkflowValidationContext"/> and aggregates findings into a single report.
///
/// Rules run sequentially in <see cref="IWorkflowValidationRule.Order"/> order; cancellation is
/// honored between rules. Unhandled exceptions from a rule become a synthetic
/// <c>pipeline-error</c> finding and the remaining rules still run.
/// </summary>
public sealed class WorkflowValidationPipeline
{
    private readonly IReadOnlyList<IWorkflowValidationRule> orderedRules;
    private readonly ILogger<WorkflowValidationPipeline> logger;
    private readonly IAuthoringTelemetry telemetry;

    public WorkflowValidationPipeline(
        IEnumerable<IWorkflowValidationRule> rules,
        ILogger<WorkflowValidationPipeline> logger,
        IAuthoringTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(telemetry);
        orderedRules = rules.OrderBy(r => r.Order).ToArray();
        this.logger = logger;
        this.telemetry = telemetry;
    }

    public async Task<WorkflowValidationReport> RunAsync(
        WorkflowValidationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<WorkflowValidationFinding>();
        foreach (var rule in orderedRules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var ruleFindings = await rule.RunAsync(context, cancellationToken);
                if (ruleFindings is { Count: > 0 })
                {
                    findings.AddRange(ruleFindings);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Backstop — a rule that throws shouldn't take down the pipeline. Surface as a
                // synthetic finding tagged with the offending rule id so the editor still has
                // something actionable, and let the remaining rules run.
                logger.LogError(ex, "Validation rule {RuleId} threw an unexpected exception", rule.RuleId);
                findings.Add(new WorkflowValidationFinding(
                    RuleId: "pipeline-error",
                    Severity: WorkflowValidationSeverity.Error,
                    Message: $"Validation rule '{rule.RuleId}' failed: {ex.Message}"));
            }
        }

        // Errors first so the editor's results panel shows blockers at the top by default.
        var ordered = findings
            .OrderByDescending(f => f.Severity)
            .ToArray();

        // O1: emit one telemetry event per finding so error-prevention rate and per-rule firing
        // can be aggregated downstream. The aggregate "save was blocked" event is emitted by the
        // caller (save endpoint) — the pipeline runs interactively too, where blocking is a
        // non-event.
        foreach (var finding in ordered)
        {
            telemetry.ValidatorFired(context.Key, finding);
        }

        return new WorkflowValidationReport(ordered);
    }
}
