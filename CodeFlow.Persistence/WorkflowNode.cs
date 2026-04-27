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
    RejectionHistoryConfig? RejectionHistory = null);
