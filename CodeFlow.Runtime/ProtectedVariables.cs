namespace CodeFlow.Runtime;

/// <summary>
/// Registry of workflow-variable keys that the framework owns and seeds on the runtime's
/// behalf (e.g. <c>workDir</c>, set by the trace-launch endpoint). Both the script host's
/// <c>setWorkflow</c> verb and the agent-side <c>setWorkflow</c> tool reject writes to these
/// keys so workflow authors can rely on framework-managed values being stable for the
/// life of the trace.
/// </summary>
public static class ProtectedVariables
{
    public static IReadOnlySet<string> ReservedKeys { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "workDir",
            "traceId",
        };

    public static bool IsReserved(string key) => ReservedKeys.Contains(key);
}
