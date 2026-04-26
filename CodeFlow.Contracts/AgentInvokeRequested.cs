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
    int? ReviewMaxRounds = null);
