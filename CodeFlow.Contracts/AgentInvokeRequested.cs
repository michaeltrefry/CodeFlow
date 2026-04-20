namespace CodeFlow.Contracts;

public sealed record AgentInvokeRequested(
    Guid TraceId,
    Guid RoundId,
    string WorkflowKey,
    int WorkflowVersion,
    string AgentKey,
    int AgentVersion,
    Uri InputRef,
    IReadOnlyDictionary<string, string>? CorrelationHeaders = null,
    RetryContext? RetryContext = null);
