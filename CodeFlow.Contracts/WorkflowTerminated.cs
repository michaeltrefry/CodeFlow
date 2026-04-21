namespace CodeFlow.Contracts;

public enum WorkflowTerminalKind
{
    Completed,
    Failed,
    Escalated,
}

public sealed record WorkflowTerminated(
    Guid TraceId,
    string WorkflowKey,
    int WorkflowVersion,
    WorkflowTerminalKind Kind,
    DateTime TerminatedAtUtc);
