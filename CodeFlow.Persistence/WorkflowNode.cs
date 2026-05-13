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
    // Swarm nodes: "Sequential" (sc-43) or "Coordinator" (sc-46). Closed enum stored as string;
    // null on every other kind. Both protocols are dispatched by the runtime.
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
    int? SwarmTokenBudget = null,
    // ForEach nodes (sc-942): Scriban expression evaluated against workflow context that yields
    // the collection to iterate. Required and non-empty for ForEach; null on every other kind.
    string? CollectionExpression = null,
    // ForEach nodes (sc-942): author-chosen identifier bound under `loop.item` in the child
    // Scriban scope. Defaults to "item" at the API/UI layer; null on every other kind.
    string? ItemVar = null,
    // Goal nodes (epic 978): objective text the goal-runner agent pursues. Scriban-rendered
    // against `workflow.*` at runtime so authors can plug in story titles, requirement docs,
    // etc. Required on Goal; null on every other kind.
    string? GoalObjective = null,
    // Goal nodes (epic 978): optional cumulative token cap across all iterations of the goal
    // loop. System-enforced — the model can read it via `goal.get` but cannot self-pause on
    // budget pressure. Null = unbounded; > 0 when set.
    int? GoalTokenBudget = null,
    // Goal nodes (epic 978): safety-net cap on continuation iterations within the goal loop.
    // Token budget is the primary cap; this exists so a runaway prompt cannot loop forever.
    // Null defaults to 50 at runtime; must be > 0 when set.
    int? GoalMaxIterations = null);
