using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

/// <summary>
/// Owns the <c>setContext</c> / <c>setWorkflow</c> tool path: argument parsing, key/value
/// validation, per-call and cumulative size caps, reserved-key enforcement, and the
/// pending-bag write itself. Also exposes the staging primitive that host tools (e.g.
/// <c>setup_workspace</c>, sc-680) use to push workflow-bag writes through the same
/// validation contract via <see cref="ToolExecutionContext.StageWorkflowBagWrite"/>.
///
/// <para>
/// Carved out of <c>InvocationLoop</c> (sc-177) so the bag-write contract is testable in
/// isolation and the loop driver doesn't have to know about size caps or reserved keys.
/// </para>
/// </summary>
internal static class ContextUpdateHandler
{
    /// <summary>
    /// Cap on the serialized size of accumulated <c>setContext</c> / <c>setWorkflow</c> writes
    /// per invocation. Mirrors the cap on <see cref="LogicNodeScriptHost"/>'s Logic-node
    /// counterpart so agents and Logic nodes share the same bag-write budget. Exceeding the
    /// cap returns a tool error and the loop continues — the offending value is not persisted.
    /// </summary>
    private const int MaxContextUpdatesChars = 256 * 1024;

    /// <summary>
    /// Cap on the length of a single <c>setContext</c> / <c>setWorkflow</c> key, mirroring the
    /// Logic-node validation guard. Keys above this length are rejected with a tool error.
    /// </summary>
    private const int MaxContextKeyChars = 256;

    /// <summary>
    /// Cap on the serialized length of a single <c>setContext</c> / <c>setWorkflow</c> value
    /// (per-call). Closes the runtime failure mode where an agent tries to stream a large
    /// document (PRD, plan, codebase chunk) into the workflow bag mid-turn — the model has to
    /// emit the entire value as JSON tool-call args, eating into <c>max_tokens</c> and producing
    /// a <see cref="JsonReaderException"/> when the args JSON is truncated mid-string. The
    /// remediation is to capture the document via an output script using <c>setOutput</c> /
    /// <c>setWorkflow</c> in the script sandbox, where the value is bounded by the script's own
    /// 256 KiB budget rather than the model's per-turn token allowance.
    /// </summary>
    private const int MaxSingleWriteChars = 16 * 1024;

    /// <summary>
    /// Recognises a <c>setContext</c> or <c>setWorkflow</c> tool call and stages the requested
    /// write into the matching pending-bag dictionary. Returns false (with <paramref name="toolResult"/>
    /// null) when the tool call is not one of the recognised names — the caller falls through to
    /// the regular tool-invocation path. Any validation failure is returned as an error
    /// <see cref="ToolResult"/> with <c>IsError: true</c>; the loop continues.
    /// </summary>
    public static bool TryHandle(
        ToolCall toolCall,
        Dictionary<string, JsonElement> pendingContextUpdates,
        Dictionary<string, JsonElement> pendingWorkflowUpdates,
        out ToolResult? toolResult)
    {
        Dictionary<string, JsonElement>? targetBag = null;
        string? toolDisplayName = null;

        if (string.Equals(toolCall.Name, InvocationLoop.SetContextToolName, StringComparison.OrdinalIgnoreCase))
        {
            targetBag = pendingContextUpdates;
            toolDisplayName = InvocationLoop.SetContextToolName;
        }
        else if (string.Equals(toolCall.Name, InvocationLoop.SetWorkflowToolName, StringComparison.OrdinalIgnoreCase))
        {
            targetBag = pendingWorkflowUpdates;
            toolDisplayName = InvocationLoop.SetWorkflowToolName;
        }

        if (targetBag is null || toolDisplayName is null)
        {
            toolResult = null;
            return false;
        }

        try
        {
            var keyNode = toolCall.Arguments?["key"];
            if (keyNode is not JsonValue keyValue
                || !keyValue.TryGetValue<string>(out var key)
                || string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException($"{toolDisplayName}(key, value) requires a non-empty string key.");
            }

            if (key.Length > MaxContextKeyChars)
            {
                throw new InvalidOperationException(
                    $"{toolDisplayName} key length {key.Length} exceeds the {MaxContextKeyChars}-character cap.");
            }

            var valueNode = toolCall.Arguments?["value"];
            // Treat an explicit null value as a delete — store the JSON null so saga merge
            // overwrites the previous value with null. Missing 'value' is an error.
            if (valueNode is null && (toolCall.Arguments is null || !toolCall.Arguments.AsObject().ContainsKey("value")))
            {
                throw new InvalidOperationException($"{toolDisplayName}(key, value) requires a 'value' argument.");
            }

            var element = valueNode is null
                ? JsonDocument.Parse("null").RootElement.Clone()
                : JsonDocument.Parse(valueNode.ToJsonString()).RootElement.Clone();

            var isWorkflowWrite = string.Equals(toolDisplayName, InvocationLoop.SetWorkflowToolName, StringComparison.Ordinal);
            if (!TryStageBagWrite(targetBag, toolDisplayName, key, element, isWorkflowWrite, out var stageError))
            {
                throw new InvalidOperationException(stageError!);
            }

            toolResult = new ToolResult(toolCall.Id, $"[{toolDisplayName}({key})]");
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException or JsonException)
        {
            toolResult = new ToolResult(toolCall.Id, exception.Message, IsError: true);
            return true;
        }
    }

    /// <summary>
    /// Shared validation + commit for workflow / context bag writes. Used by the
    /// <c>setContext</c> / <c>setWorkflow</c> tool path AND by host tools that stage workflow
    /// writes via <see cref="ToolExecutionContext.StageWorkflowBagWrite"/> (sc-680:
    /// <c>setup_workspace</c>). Mirrors the per-call value cap, the cumulative pending-writes
    /// cap, and the reserved-key check exactly so both code paths share a single contract.
    /// </summary>
    public static bool TryStageBagWrite(
        Dictionary<string, JsonElement> targetBag,
        string toolDisplayName,
        string key,
        JsonElement element,
        bool isWorkflowWrite,
        out string? errorMessage)
    {
        if (isWorkflowWrite && ProtectedVariables.IsReserved(key))
        {
            errorMessage = $"setWorkflow('{key}', ...) is rejected: '{key}' is a framework-managed "
                + "workflow variable and cannot be overwritten by agents.";
            return false;
        }

        // Per-call value cap (V1). Reject values larger than the single-write budget before
        // they enter the pending bag — the agent gets a typed tool error pointing at the
        // output-script remediation path, the loop continues, and the trace doesn't risk a
        // mid-string JSON truncation when the model retries.
        var elementSize = element.GetRawText().Length;
        if (elementSize > MaxSingleWriteChars)
        {
            errorMessage = $"{toolDisplayName} value for key '{key}' is {elementSize} chars, exceeding "
                + $"the {MaxSingleWriteChars}-character per-call cap. Move large content to "
                + $"an output script using setOutput / {toolDisplayName} in the script "
                + $"sandbox; mid-turn tool-call args are constrained by the model's "
                + $"max_tokens budget.";
            return false;
        }

        var candidate = new Dictionary<string, JsonElement>(targetBag, StringComparer.Ordinal)
        {
            [key] = element
        };
        var serializedSize = JsonSerializer.Serialize(candidate).Length;
        if (serializedSize > MaxContextUpdatesChars)
        {
            errorMessage = $"{toolDisplayName} writes total {serializedSize} chars, exceeding the "
                + $"{MaxContextUpdatesChars}-character cap. Discarding this write.";
            return false;
        }

        targetBag[key] = element;
        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Builds a per-invocation <see cref="ToolExecutionContext"/> for a host-tool call that
    /// closes over the active <paramref name="pendingWorkflowUpdates"/> dictionary, exposing
    /// <see cref="ToolExecutionContext.StageWorkflowBagWrite"/> so host tools can stage
    /// workflow-bag writes through the same caps + reserved-key check that the regular
    /// <c>setWorkflow</c> tool path enforces. sc-680.
    /// </summary>
    public static ToolExecutionContext WrapForHostTool(
        ToolExecutionContext? context,
        Dictionary<string, JsonElement> pendingWorkflowUpdates)
    {
        return (context ?? new ToolExecutionContext()) with
        {
            StageWorkflowBagWrite = (key, value) =>
            {
                if (!TryStageBagWrite(
                        pendingWorkflowUpdates,
                        InvocationLoop.SetWorkflowToolName,
                        key,
                        value,
                        isWorkflowWrite: true,
                        out var stageError))
                {
                    throw new InvalidOperationException(stageError!);
                }
            },
        };
    }
}
