namespace CodeFlow.Orchestration.Scripting;

public sealed record LogicNodeEvaluationResult(
    string? OutputPortName,
    IReadOnlyList<string> LogEntries,
    TimeSpan Duration,
    LogicNodeFailureKind? Failure,
    string? FailureMessage)
{
    public bool IsSuccess => Failure is null && !string.IsNullOrWhiteSpace(OutputPortName);

    public static LogicNodeEvaluationResult Success(string port, IReadOnlyList<string> logs, TimeSpan duration) =>
        new(port, logs, duration, null, null);

    public static LogicNodeEvaluationResult Fail(LogicNodeFailureKind kind, string message, IReadOnlyList<string> logs, TimeSpan duration) =>
        new(null, logs, duration, kind, message);
}

public enum LogicNodeFailureKind
{
    Timeout,
    ScriptError,
    MissingSetNodePath,
    UnknownPort
}
