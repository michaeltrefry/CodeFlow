namespace CodeFlow.Runtime.Workspace;

public sealed record GitCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
