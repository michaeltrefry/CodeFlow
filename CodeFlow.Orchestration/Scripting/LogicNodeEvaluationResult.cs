using System.Text.Json;

namespace CodeFlow.Orchestration.Scripting;

public sealed record LogicNodeEvaluationResult(
    string? OutputPortName,
    IReadOnlyList<string> LogEntries,
    TimeSpan Duration,
    LogicNodeFailureKind? Failure,
    string? FailureMessage,
    IReadOnlyDictionary<string, JsonElement> ContextUpdates,
    IReadOnlyDictionary<string, JsonElement> GlobalUpdates)
{
    public bool IsSuccess => Failure is null && !string.IsNullOrWhiteSpace(OutputPortName);

    public static LogicNodeEvaluationResult Success(
        string port,
        IReadOnlyList<string> logs,
        TimeSpan duration,
        IReadOnlyDictionary<string, JsonElement> contextUpdates,
        IReadOnlyDictionary<string, JsonElement> globalUpdates) =>
        new(port, logs, duration, null, null, contextUpdates, globalUpdates);

    public static LogicNodeEvaluationResult Fail(
        LogicNodeFailureKind kind,
        string message,
        IReadOnlyList<string> logs,
        TimeSpan duration) =>
        new(null, logs, duration, kind, message, EmptyContextUpdates, EmptyContextUpdates);

    internal static readonly IReadOnlyDictionary<string, JsonElement> EmptyContextUpdates =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public enum LogicNodeFailureKind
{
    Timeout,
    ScriptError,
    MissingSetNodePath,
    UnknownPort,
    ContextBudgetExceeded
}
