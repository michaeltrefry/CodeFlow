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
    IReadOnlyDictionary<string, string>? OutputPortReplacements = null);
