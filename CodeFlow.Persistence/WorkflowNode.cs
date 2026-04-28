namespace CodeFlow.Persistence;

public sealed record WorkflowNode(
    Guid Id,
    WorkflowNodeKind Kind,
    string? AgentKey,
    int? AgentVersion,
    string? OutputScript,
    IReadOnlyList<string> OutputPorts,
    double LayoutX,
    double LayoutY,
    string? SubflowKey = null,
    int? SubflowVersion = null,
    int? ReviewMaxRounds = null,
    string? LoopDecision = null,
    string? InputScript = null,
    bool OptOutLastRoundReminder = false,
    RejectionHistoryConfig? RejectionHistory = null,
    // P4: when set on an Agent/Hitl/Start node, the runtime mirrors the agent's output
    // text into this workflow variable BEFORE the node's output script runs (so the script
    // can read `workflow[mirrorKey]`). Replaces the hand-rolled Pattern-1 output script.
    string? MirrorOutputToWorkflowVar = null,
    // P5: per-port map of "if this port is taken, replace the downstream artifact with the
    // contents of workflow[<value>] after the output script runs". Replaces the hand-rolled
    // Pattern-2 output script. Only port names that have a binding need to appear in the dict.
    IReadOnlyDictionary<string, string>? OutputPortReplacements = null,
    // Transform nodes: Scriban template body rendered against `input.* + context.* + workflow.*`.
    // Required and must Scriban-parse on save for Transform; null on every other node kind.
    string? Template = null,
    // Transform nodes: "string" (default) — rendered text becomes the Out artifact verbatim.
    // "json" mode (deserialize rendered text before emitting) is gated behind TN-2.
    string OutputType = "string",
    // Swarm nodes (sc-43): "Sequential" or "Coordinator". Closed enum stored as string. Null on
    // every other kind. The Coordinator runtime ships in sc-46; v1 only Sequential is dispatchable.
    string? SwarmProtocol = null,
    // Swarm nodes: number of contributors (Sequential) or max workers (Coordinator). 1..16.
    int? SwarmN = null,
    // Swarm nodes: agent key/version used for every contributor position (and every Coordinator
    // worker position). Single role, reused — per-position differentiation comes from the prompt
    // template's swarmPosition / swarmContributions inputs, not from separate roles.
    string? ContributorAgentKey = null,
    int? ContributorAgentVersion = null,
    // Swarm nodes: agent key/version for the final synthesis step. Runs once after all
    // contributors complete and produces the node's terminal artifact.
    string? SynthesizerAgentKey = null,
    int? SynthesizerAgentVersion = null,
    // Swarm-Coordinator nodes only: the agent that runs first and returns assignments. Validator
    // requires non-null when SwarmProtocol == "Coordinator" and rejects non-null on Sequential.
    string? CoordinatorAgentKey = null,
    int? CoordinatorAgentVersion = null,
    // Swarm nodes: optional cumulative token cap (input + output tokens summed across coordinator,
    // contributors, and synthesizer for this node only). Null = unbounded. > 0 when set. The
    // saga checks the budget after each contributor completion and may run the synthesizer early
    // with swarmEarlyTerminated = true. See docs/swarm-node.md §"Token budget".
    int? SwarmTokenBudget = null);
