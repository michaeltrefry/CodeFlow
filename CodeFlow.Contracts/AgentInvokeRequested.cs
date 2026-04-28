using System.Text.Json;

namespace CodeFlow.Contracts;

public sealed record AgentInvokeRequested(
    Guid TraceId,
    Guid RoundId,
    string WorkflowKey,
    int WorkflowVersion,
    Guid NodeId,
    string AgentKey,
    int AgentVersion,
    Uri InputRef,
    IReadOnlyDictionary<string, JsonElement> ContextInputs,
    IReadOnlyDictionary<string, string>? CorrelationHeaders = null,
    RetryContext? RetryContext = null,
    ToolExecutionContext? ToolExecutionContext = null,
    IReadOnlyDictionary<string, JsonElement>? WorkflowContext = null,
    int? ReviewRound = null,
    int? ReviewMaxRounds = null,
    // P2: when set on a node inside a ReviewLoop child saga, suppresses runtime injection of
    // the @codeflow/last-round-reminder partial. Default false: agents inside loops get the
    // reminder unless the author explicitly opts out on the workflow node.
    bool OptOutLastRoundReminder = false,
    // sc-43 Swarm node: populated when the saga dispatches a contributor / coordinator /
    // synthesizer inside a Swarm node. Renderer maps these to top-level prompt-template
    // variables (`swarmPosition`, `swarmAssignment`, `swarmMaxN`, `swarmEarlyTerminated`) the
    // same way `ReviewRound` becomes `round`. Null on every non-Swarm dispatch.
    SwarmInvocationContext? SwarmContext = null);

/// <summary>
/// Per-invocation Swarm context piggybacked onto <see cref="AgentInvokeRequested"/> when the
/// dispatching saga is inside a Swarm node (sc-43 Sequential, sc-46 Coordinator). Each field
/// maps directly to a top-level prompt-template variable the contributor / coordinator /
/// synthesizer agent can read. All fields nullable so an agent prompt can branch with
/// <c>{{ if swarmPosition }}</c>.
/// </summary>
/// <param name="Position">1-indexed contributor / worker position. Set on contributor + worker
/// dispatches; null on synthesizer and coordinator dispatches.</param>
/// <param name="MaxN">The configured <c>n</c> cap. Set on contributor + coordinator dispatches;
/// null on synthesizer.</param>
/// <param name="Assignment">Coordinator-mode worker assignment payload (free-form string the
/// coordinator emitted for this position). Null in Sequential mode and on non-worker dispatches.</param>
/// <param name="EarlyTerminated">True when the synthesizer is being dispatched after the token
/// budget was exceeded mid-swarm; the synthesizer's prompt can branch on this to flag the
/// partial input. False (or null) on every other dispatch.</param>
public sealed record SwarmInvocationContext(
    int? Position,
    int? MaxN,
    string? Assignment,
    bool? EarlyTerminated);
