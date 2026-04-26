namespace CodeFlow.Api.Validation.Pipeline;

/// <summary>
/// Structured authoring-time telemetry sink. Used by the validation pipeline to emit one event
/// per finding and an aggregate event when a save is blocked, and by built-in workflow features
/// (P1-P5 in the Workflow Authoring DX epic) to record adoption vs. hand-rolled equivalents.
///
/// The default implementation writes structured ILogger entries (queryable in any log
/// aggregator) — no separate events pipeline. Tests substitute a recording fake.
/// </summary>
public interface IAuthoringTelemetry
{
    /// <summary>
    /// Emit one event per validation finding. Fires for both warnings and errors so the
    /// adoption / error-prevention rate can be measured separately from outright save blocks.
    /// </summary>
    void ValidatorFired(string workflowKey, WorkflowValidationFinding finding);

    /// <summary>
    /// Emit one aggregate event when a save attempt is rejected because the validation pipeline
    /// produced at least one error-severity finding. The caller — typically a save endpoint —
    /// decides when to call this; the pipeline itself only knows about the finding shape.
    /// </summary>
    /// <param name="workflowKey">Key of the workflow that failed to save.</param>
    /// <param name="errorRuleIds">Distinct rule ids that contributed at least one error.</param>
    void ValidatorBlockedSave(string workflowKey, IReadOnlyList<string> errorRuleIds);

    /// <summary>
    /// Emit one event when a built-in feature instance is exercised at runtime (per node, per
    /// trace) so adoption can be tracked against equivalent hand-rolled patterns. Reserved for
    /// the Phase 3 feature cards (P1-P5); the validator pipeline does not call this.
    /// </summary>
    /// <param name="workflowKey">Key of the workflow whose run exercised the feature.</param>
    /// <param name="featureId">Stable identifier for the feature, e.g. <c>mirror-to-workflow-var</c>.</param>
    /// <param name="instances">How many separate instances within the trace exercised the feature
    /// (typically 1 per node-per-trace; bulk emitters can pass higher counts).</param>
    void FeatureUsed(string workflowKey, string featureId, int instances = 1);
}
