namespace CodeFlow.Runtime;

public interface IAgentInvoker
{
    Task<AgentInvocationResult> InvokeAsync(
        AgentInvocationConfiguration configuration,
        string? input,
        ResolvedAgentTools tools,
        CancellationToken cancellationToken = default,
        ToolExecutionContext? toolExecutionContext = null);

    /// <summary>
    /// Observer-aware overload. The saga-side <c>AgentInvocationConsumer</c> uses this to wire a
    /// token-usage capture observer per consumer call so each LLM round-trip writes a
    /// <c>TokenUsageRecord</c> attributed to the originating trace + node + scope chain.
    /// Default implementation ignores the observer and delegates to the legacy overload — only
    /// the production <c>Agent</c> class threads the observer all the way through to
    /// <c>InvocationLoop</c>; test fakes that don't model the InvocationLoop accept the default.
    /// </summary>
    Task<AgentInvocationResult> InvokeAsync(
        AgentInvocationConfiguration configuration,
        string? input,
        ResolvedAgentTools tools,
        IInvocationObserver? observer,
        CancellationToken cancellationToken = default,
        ToolExecutionContext? toolExecutionContext = null)
        => InvokeAsync(configuration, input, tools, cancellationToken, toolExecutionContext);
}
