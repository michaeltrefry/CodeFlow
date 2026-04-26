namespace CodeFlow.Runtime;

/// <summary>
/// Thrown when the <see cref="InvocationLoop"/> is about to send a request to a model
/// provider but the conversation transcript contains an assistant <c>function_call</c>
/// without a matching <c>function_call_output</c> tool message. Both the OpenAI Responses
/// API and equivalent Anthropic surface enforce this pairing — emitting an unpaired
/// assistant tool-call would produce a provider-side error like
/// <c>No tool output found for function call &lt;id&gt;</c>. Detecting it client-side gives
/// us a stack trace that points at the offending retry path instead of an opaque HTTP
/// failure surfaced two layers down.
/// </summary>
public sealed class OrphanFunctionCallException : InvalidOperationException
{
    public OrphanFunctionCallException(IReadOnlyList<string> orphanedCallIds)
        : base(BuildMessage(orphanedCallIds))
    {
        OrphanedCallIds = orphanedCallIds;
    }

    public IReadOnlyList<string> OrphanedCallIds { get; }

    private static string BuildMessage(IReadOnlyList<string> orphanedCallIds)
    {
        ArgumentNullException.ThrowIfNull(orphanedCallIds);
        return "InvocationLoop transcript contains assistant function_call(s) with no matching "
            + "function_call_output tool message: ["
            + string.Join(", ", orphanedCallIds)
            + "]. Every retry/reminder path that pushes a User message after an assistant tool "
            + "call must first append a Tool message with the matching ToolCallId.";
    }
}
