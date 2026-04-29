using System.Text.Json;
using CodeFlow.Runtime;

namespace CodeFlow.Orchestration;

/// <summary>
/// Per-port-model agent output transforms shared by <see cref="WorkflowSagaStateMachine"/> and
/// <see cref="DryRun.DryRunExecutor"/>. F-009 in the 2026-04-28 backend review.
/// <list type="bullet">
///   <item><description><b>P4</b> (mirror): copy the agent's output text into a configured
///     workflow variable BEFORE the output script runs so the script can read it.</description></item>
///   <item><description><b>P5</b> (port replacement): swap the agent's artifact for the contents
///     of a named workflow variable when the resolved output port has a configured binding.</description></item>
/// </list>
/// Both transforms encode the same precedence + normalisation rules — extracting them here
/// guarantees saga and dry-run can't drift on user-observable port semantics. Each call site
/// keeps its own side-effect (saga writes an override artifact via <see cref="IArtifactStore"/>;
/// dry-run swaps the in-memory string).
/// </summary>
public static class AgentOutputTransforms
{
    /// <summary>
    /// Normalize the configured mirror-target key. Returns <c>null</c> when unset,
    /// whitespace-only, or pointing at a framework-managed reserved variable
    /// (<c>__loop.*</c>); otherwise returns the trimmed key.
    /// </summary>
    public static string? NormalizeMirrorTarget(string? mirrorTarget)
    {
        if (string.IsNullOrWhiteSpace(mirrorTarget))
        {
            return null;
        }

        var trimmed = mirrorTarget.Trim();
        // P4: a configured key targeting the framework-managed __loop.* namespace fails silently
        // — the save-time validator surfaces the misconfiguration; the runtime never clobbers
        // framework state. (Mirrors the protection the agent-side setWorkflow tool enforces.)
        if (ProtectedVariables.IsReserved(trimmed))
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// Normalize the per-port replacement map: trim keys/values, drop blanks. Returns
    /// <c>null</c> when no usable entries remain.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? NormalizePortReplacements(
        IReadOnlyDictionary<string, string>? portReplacements)
    {
        if (portReplacements is null || portReplacements.Count == 0)
        {
            return null;
        }

        Dictionary<string, string>? normalized = null;
        foreach (var (port, key) in portReplacements)
        {
            if (string.IsNullOrWhiteSpace(port) || string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            normalized ??= new Dictionary<string, string>(StringComparer.Ordinal);
            normalized[port.Trim()] = key.Trim();
        }

        return normalized is { Count: > 0 } ? normalized : null;
    }

    /// <summary>
    /// P4 apply: return a new dictionary with the agent's output text serialized as a JSON
    /// string element under <paramref name="mirrorKey"/>. The original map is not mutated
    /// (the saga reserializes to <c>InputsJson</c>; the dry-run writes through to its own
    /// mutable bag).
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> Mirror(
        IReadOnlyDictionary<string, JsonElement> workflowVars,
        string mirrorKey,
        string outputText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorKey);
        ArgumentNullException.ThrowIfNull(outputText);

        var bag = new Dictionary<string, JsonElement>(workflowVars, StringComparer.Ordinal)
        {
            [mirrorKey] = JsonSerializer.SerializeToElement(outputText),
        };
        return bag;
    }

    /// <summary>
    /// P5 lookup: resolve the replacement text for the resolved output port. Returns
    /// <c>null</c> when the port has no binding, the bound variable is missing from
    /// <paramref name="workflowVars"/>, or its value is <see cref="JsonValueKind.Null"/> /
    /// <see cref="JsonValueKind.Undefined"/>. String values are returned as-is; everything
    /// else as raw JSON text. Configured-but-unset is fail-safe — keep the agent's artifact
    /// rather than substituting an empty string.
    /// </summary>
    public static string? TryGetPortReplacement(
        IReadOnlyDictionary<string, string> portReplacementsByPort,
        string? resolvedPort,
        IReadOnlyDictionary<string, JsonElement> workflowVars)
    {
        ArgumentNullException.ThrowIfNull(portReplacementsByPort);
        ArgumentNullException.ThrowIfNull(workflowVars);

        if (string.IsNullOrWhiteSpace(resolvedPort)
            || !portReplacementsByPort.TryGetValue(resolvedPort, out var workflowKey))
        {
            return null;
        }

        if (!workflowVars.TryGetValue(workflowKey, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };
    }

    /// <summary>
    /// Convenience: combine the lookup + the bound-variable-name in one call so callers can
    /// log which variable supplied the replacement. Returns <c>(null, null)</c> when no
    /// replacement applies.
    /// </summary>
    public static (string? ReplacementText, string? BoundVariableName) ResolvePortReplacement(
        IReadOnlyDictionary<string, string> portReplacementsByPort,
        string? resolvedPort,
        IReadOnlyDictionary<string, JsonElement> workflowVars)
    {
        if (string.IsNullOrWhiteSpace(resolvedPort)
            || !portReplacementsByPort.TryGetValue(resolvedPort, out var workflowKey))
        {
            return (null, null);
        }

        var text = TryGetPortReplacement(portReplacementsByPort, resolvedPort, workflowVars);
        return text is null ? (null, null) : (text, workflowKey);
    }
}
