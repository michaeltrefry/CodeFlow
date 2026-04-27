namespace CodeFlow.Runtime;

/// <summary>
/// Registry of workflow-variable keys that the framework owns and seeds on the runtime's
/// behalf (e.g. <c>workDir</c>, set by the trace-launch endpoint). Both the script host's
/// <c>setWorkflow</c> verb and the agent-side <c>setWorkflow</c> tool reject writes to these
/// keys so workflow authors can rely on framework-managed values being stable for the
/// life of the trace.
///
/// <see cref="ReservedNamespaces"/> reserves entire dotted prefixes — any key that begins
/// with <c>{namespace}.</c> is rejected. P3 adds <c>__loop</c> so the runtime owns
/// <c>__loop.rejectionHistory</c> and any future loop-managed bag accumulators.
/// </summary>
public static class ProtectedVariables
{
    public static IReadOnlySet<string> ReservedKeys { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "workDir",
            "traceId",
        };

    public static IReadOnlySet<string> ReservedNamespaces { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "__loop",
        };

    public static bool IsReserved(string key)
    {
        if (ReservedKeys.Contains(key))
        {
            return true;
        }

        foreach (var ns in ReservedNamespaces)
        {
            if (key.Length > ns.Length
                && key[ns.Length] == '.'
                && key.AsSpan(0, ns.Length).SequenceEqual(ns.AsSpan()))
            {
                return true;
            }
        }

        return false;
    }
}
