using System.Diagnostics;
using CodeFlow.Runtime.Observability;

namespace CodeFlow.Runtime;

/// <summary>
/// Wraps <see cref="ToolRegistry"/> with the runtime's tool-invocation cross-cutting:
/// <c>tool.call</c> activity span tagged with the tool name, exception → tool_output
/// marshalling so a thrown exception becomes a tool error rather than crashing the loop,
/// and observability status updates.
///
/// <para>
/// Carved out of <c>InvocationLoop</c> (sc-177) so the dispatch + exception contract is
/// testable in isolation.
/// </para>
/// </summary>
internal sealed class ToolInvoker
{
    private readonly ToolRegistry toolRegistry;

    public ToolInvoker(ToolRegistry toolRegistry)
    {
        this.toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
    }

    public async Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        ToolAccessPolicy? policy,
        CancellationToken cancellationToken,
        ToolExecutionContext? context)
    {
        using var activity = CodeFlowActivity.StartChild("tool.call");
        activity?.SetTag(CodeFlowActivity.TagNames.ToolName, toolCall.Name);

        try
        {
            var result = await toolRegistry.InvokeAsync(toolCall, policy, cancellationToken, context);
            if (result.IsError)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "tool reported error");
            }
            else
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            return new ToolResult(toolCall.Id, exception.Message, IsError: true);
        }
    }
}
