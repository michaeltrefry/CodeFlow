using Microsoft.Extensions.Logging;

namespace CodeFlow.Api.Validation.Pipeline;

/// <summary>
/// Default <see cref="IAuthoringTelemetry"/> implementation. Writes one structured log entry per
/// event using stable event names — <c>workflow.validator.fired</c>,
/// <c>workflow.validator.blocked_save</c>, <c>workflow.feature.used</c> — and stable property
/// keys so log aggregators can group/aggregate without parsing message text.
/// </summary>
public sealed class LoggerAuthoringTelemetry : IAuthoringTelemetry
{
    private readonly ILogger<LoggerAuthoringTelemetry> logger;

    public LoggerAuthoringTelemetry(ILogger<LoggerAuthoringTelemetry> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    public void ValidatorFired(string workflowKey, WorkflowValidationFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        logger.LogInformation(
            "workflow.validator.fired {WorkflowKey} {RuleId} {Severity} {NodeId}",
            workflowKey,
            finding.RuleId,
            finding.Severity,
            finding.Location?.NodeId);
    }

    public void ValidatorBlockedSave(string workflowKey, IReadOnlyList<string> errorRuleIds)
    {
        ArgumentNullException.ThrowIfNull(errorRuleIds);
        logger.LogWarning(
            "workflow.validator.blocked_save {WorkflowKey} {ErrorRuleIds}",
            workflowKey,
            string.Join(",", errorRuleIds));
    }

    public void FeatureUsed(string workflowKey, string featureId, int instances = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        logger.LogInformation(
            "workflow.feature.used {WorkflowKey} {FeatureId} {Instances}",
            workflowKey,
            featureId,
            instances);
    }
}
