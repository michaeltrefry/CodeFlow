using CodeFlow.Persistence;

namespace CodeFlow.Orchestration.Scripting;

/// <summary>
/// F2 (Workflow Authoring DX): per-node static-analysis snapshot describing what's in scope
/// when this node executes. Computed from the workflow graph's reachable upstream nodes plus
/// best-effort JS AST parsing of their input / output scripts.
///
/// Confidence:
/// <list type="bullet">
///   <item><description><b>Definite</b> — at least one upstream node unconditionally writes
///   the variable on every reachable execution path (today: any top-level <c>setWorkflow</c>
///   / <c>setContext</c> call in an upstream script counts as definite).</description></item>
///   <item><description><b>Conditional</b> — at least one upstream node may write the
///   variable but only on some paths (today: any <c>setWorkflow</c> / <c>setContext</c> call
///   inside an <c>if</c> / loop / try block in an upstream script).</description></item>
/// </list>
/// </summary>
public sealed record NodeDataflowScope(
    Guid NodeId,
    IReadOnlyList<DataflowVariable> WorkflowVariables,
    IReadOnlyList<DataflowVariable> ContextKeys,
    DataflowInputSource? InputSource,
    DataflowLoopBindings? LoopBindings);

public sealed record DataflowVariable(
    string Key,
    DataflowConfidence Confidence,
    IReadOnlyList<DataflowVariableSource> Sources);

public enum DataflowConfidence
{
    Definite,
    Conditional,
}

public sealed record DataflowVariableSource(
    Guid NodeId,
    string ScriptKind);

public sealed record DataflowInputSource(
    Guid NodeId,
    string Port);

public sealed record DataflowLoopBindings(
    int? StaticRound,
    int MaxRounds);

public sealed record WorkflowDataflowSnapshot(
    string WorkflowKey,
    int WorkflowVersion,
    IReadOnlyDictionary<Guid, NodeDataflowScope> ScopesByNode,
    IReadOnlyList<WorkflowDataflowDiagnostic> Diagnostics);

public sealed record WorkflowDataflowDiagnostic(
    Guid NodeId,
    string ScriptKind,
    string Message);
