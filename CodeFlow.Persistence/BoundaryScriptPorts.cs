namespace CodeFlow.Persistence;

/// <summary>
/// Computes the declared-port set passed to <c>LogicNodeScriptHost.Evaluate</c> for a boundary
/// (Subflow / ReviewLoop) node's input or output script. This is the set of port names a script
/// may legitimately route to via <c>setNodePath</c> — it must include the implicit <c>Failed</c>
/// port (which the validator forbids declaring on a node) and, for ReviewLoop, the synthesized
/// <c>Exhausted</c> port plus the resolved <c>loopDecision</c> port.
///
/// Centralized here so the saga, the DryRunExecutor, and the editor's <c>/validate-script</c>
/// endpoint all agree on the same wirable set. Drift between those call sites would silently let
/// scripts pick ports the runtime would later reject (or vice versa).
/// </summary>
public static class BoundaryScriptPorts
{
    /// <summary>The implicit error-sink port present on every node. Authors cannot declare it on
    ///   <see cref="WorkflowNode.OutputPorts"/>, but a boundary script can route to it.</summary>
    public const string ImplicitFailedPort = "Failed";

    /// <summary>The synthesized exit port a ReviewLoop emits when the round budget is
    ///   exhausted. Authors cannot declare it on <c>OutputPorts</c>; the boundary output script
    ///   may route to it explicitly.</summary>
    public const string ReviewLoopExhaustedPort = "Exhausted";

    /// <summary>Default <c>LoopDecision</c> applied to a ReviewLoop when the author has not
    ///   overridden it. Mirrors the saga's <c>WorkflowSagaStateMachine.DefaultLoopDecision</c>;
    ///   kept here as a constant so the persistence layer doesn't need to depend on
    ///   orchestration.</summary>
    public const string DefaultLoopDecisionPort = "Rejected";

    /// <summary>
    /// Returns the declared-port set for a boundary script on <paramref name="boundaryNode"/>.
    /// Combines the node's author-declared <c>OutputPorts</c> with the implicit <c>Failed</c> and
    /// — for ReviewLoop — the synthesized <c>Exhausted</c> and the resolved <c>LoopDecision</c>
    /// port. When <paramref name="childTerminals"/> is supplied (server-side validation paths
    /// that have already resolved the child workflow), the child's terminal ports are folded in
    /// too; saga / dry-run callers can pass <c>null</c> and trust the author-declared set.
    /// </summary>
    /// <param name="boundaryNode">The Subflow or ReviewLoop node carrying the boundary script.
    ///   Calling this on any other node kind returns <c>OutputPorts ∪ Failed</c> for forward
    ///   compatibility but the boundary script slots are only wired for these two kinds today.</param>
    /// <param name="childTerminals">Optional child-workflow terminal-port set, when the caller
    ///   has already resolved it. Folded into the result so a script may target a terminal port
    ///   the author hasn't yet propagated onto <c>OutputPorts</c>.</param>
    public static IReadOnlyCollection<string> GetDeclaredPorts(
        WorkflowNode boundaryNode,
        IReadOnlyCollection<string>? childTerminals = null)
    {
        ArgumentNullException.ThrowIfNull(boundaryNode);

        var ports = new HashSet<string>(StringComparer.Ordinal) { ImplicitFailedPort };

        if (boundaryNode.OutputPorts is { Count: > 0 } declared)
        {
            foreach (var port in declared)
            {
                if (!string.IsNullOrWhiteSpace(port))
                {
                    ports.Add(port);
                }
            }
        }

        if (childTerminals is not null)
        {
            foreach (var port in childTerminals)
            {
                if (!string.IsNullOrWhiteSpace(port))
                {
                    ports.Add(port);
                }
            }
        }

        if (boundaryNode.Kind == WorkflowNodeKind.ReviewLoop)
        {
            ports.Add(ReviewLoopExhaustedPort);
            var loopDecision = string.IsNullOrWhiteSpace(boundaryNode.LoopDecision)
                ? DefaultLoopDecisionPort
                : boundaryNode.LoopDecision!.Trim();
            ports.Add(loopDecision);
        }

        return ports;
    }
}
