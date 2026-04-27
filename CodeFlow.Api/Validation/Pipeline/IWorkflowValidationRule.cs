namespace CodeFlow.Api.Validation.Pipeline;

/// <summary>
/// One rule in the workflow validation pipeline. Implementations are stateless; the pipeline
/// invokes <see cref="RunAsync"/> per validation request, passing a fresh context.
///
/// Rules MUST NOT throw for routine inputs — invalid graphs should produce findings, not
/// exceptions. The pipeline catches unexpected exceptions and synthesizes a single Error
/// finding tagged with the rule id, but that's a backstop, not a contract.
/// </summary>
public interface IWorkflowValidationRule
{
    /// <summary>
    /// Stable identifier for this rule, used to group findings in the editor and to aggregate
    /// telemetry. Choose kebab-case (e.g. <c>port-coupling</c>, <c>missing-role</c>).
    /// </summary>
    string RuleId { get; }

    /// <summary>
    /// Relative ordering hint. Rules with a lower order run first; rules with the same order run
    /// in registration order. Used so cheap structural rules can run before expensive cross-entity
    /// rules; aggregation is otherwise order-independent.
    /// </summary>
    int Order => 100;

    /// <summary>
    /// Inspect <paramref name="context"/> and return zero or more findings. Empty / null = pass.
    /// </summary>
    Task<IReadOnlyList<WorkflowValidationFinding>> RunAsync(
        WorkflowValidationContext context,
        CancellationToken cancellationToken);
}
