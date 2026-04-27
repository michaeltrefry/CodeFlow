using CodeFlow.Persistence;

namespace CodeFlow.Orchestration.Scripting;

/// <summary>
/// F2: produces a per-workflow-version static-analysis snapshot describing what each node
/// can read at runtime (workflow variables, context keys, input source, loop bindings).
///
/// Snapshots are cached by <c>(workflowKey, workflowVersion)</c>; immutable workflow versions
/// mean the cache key is stable for the lifetime of the version. The cache invalidates
/// naturally when a new version is created (different key); explicit invalidation isn't needed.
/// </summary>
public interface IWorkflowDataflowAnalyzer
{
    WorkflowDataflowSnapshot Analyze(Workflow workflow);

    NodeDataflowScope? GetScope(Workflow workflow, Guid nodeId);
}
