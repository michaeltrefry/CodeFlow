namespace CodeFlow.Runtime.Workspace;

public sealed class GitCommandException : Exception
{
    public GitCommandException(
        IReadOnlyList<string> arguments,
        int exitCode,
        string standardOutput,
        string standardError)
        : base(BuildMessage(arguments, exitCode, standardError))
    {
        Arguments = arguments;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public IReadOnlyList<string> Arguments { get; }

    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    private static string BuildMessage(IReadOnlyList<string> arguments, int exitCode, string stderr)
    {
        var argSummary = string.Join(' ', arguments);
        var trimmed = stderr.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? $"git {argSummary} exited with code {exitCode}."
            : $"git {argSummary} exited with code {exitCode}: {trimmed}";
    }
}
