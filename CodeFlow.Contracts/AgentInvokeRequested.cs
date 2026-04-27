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
    bool OptOutLastRoundReminder = false);
