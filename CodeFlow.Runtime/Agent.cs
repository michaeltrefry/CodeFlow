using System.Diagnostics;
using CodeFlow.Runtime.Observability;

namespace CodeFlow.Runtime;

public sealed class Agent : IAgentInvoker
{
    private readonly ContextAssembler contextAssembler;
    private readonly ModelClientRegistry modelClients;
    private readonly HostToolProvider hostToolProvider;
    private readonly IMcpClient? mcpClient;
    private readonly Func<DateTimeOffset> nowProvider;

    public Agent(
        ModelClientRegistry modelClients,
        ContextAssembler? contextAssembler = null,
        HostToolProvider? hostToolProvider = null,
        IMcpClient? mcpClient = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.modelClients = modelClients ?? throw new ArgumentNullException(nameof(modelClients));
        this.contextAssembler = contextAssembler ?? new ContextAssembler();
        this.hostToolProvider = hostToolProvider ?? new HostToolProvider();
        this.mcpClient = mcpClient;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<AgentInvocationResult> InvokeAsync(
        AgentInvocationConfiguration configuration,
        string? input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        using var activity = CodeFlowActivity.StartChild("agent.invoke");
        activity?.SetTag(CodeFlowActivity.TagNames.AgentProvider, configuration.Provider);
        activity?.SetTag(CodeFlowActivity.TagNames.AgentModel, configuration.Model);
        if (configuration.RetryContext is { } retryContext)
        {
            activity?.SetTag(CodeFlowActivity.TagNames.RetryAttempt, retryContext.AttemptNumber);
        }

        var modelClient = modelClients.Resolve(configuration.Provider);
        var messages = contextAssembler.Assemble(new ContextAssemblyRequest(
            configuration.SystemPrompt,
            configuration.PromptTemplate,
            input,
            configuration.History,
            configuration.Variables,
            configuration.RetryContext));

        var toolRegistry = new ToolRegistry(BuildProviders(configuration));
        var invocationLoop = new InvocationLoop(modelClient, toolRegistry, nowProvider);
        var loopResult = await invocationLoop.RunAsync(
            new InvocationLoopRequest(
                messages,
                configuration.Model,
                configuration.ToolAccessPolicy,
                configuration.Budget,
                configuration.MaxTokens,
                configuration.Temperature),
            cancellationToken);

        activity?.SetTag(CodeFlowActivity.TagNames.DecisionKind, loopResult.Decision.Kind.ToString());
        if (loopResult.Decision is FailedDecision failed)
        {
            activity?.SetTag(CodeFlowActivity.TagNames.FailureReason, failed.Reason);
            activity?.SetStatus(ActivityStatusCode.Error, failed.Reason);
        }
        else
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        return new AgentInvocationResult(
            loopResult.Output,
            loopResult.Decision,
            loopResult.Transcript,
            loopResult.TokenUsage,
            loopResult.ToolCallsExecuted);
    }

    private IEnumerable<IToolProvider> BuildProviders(AgentInvocationConfiguration configuration)
    {
        if (configuration.EnableHostTools)
        {
            yield return hostToolProvider;
        }

        if (configuration.SubAgents is { Count: > 0 })
        {
            yield return new SubAgentToolProvider(this, configuration.SubAgents);
        }

        if (configuration.McpTools is { Count: > 0 })
        {
            if (mcpClient is null)
            {
                throw new InvalidOperationException("MCP tools were configured, but no IMcpClient is registered.");
            }

            yield return new McpToolProvider(mcpClient, configuration.McpTools);
        }
    }
}
