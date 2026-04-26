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

    public Task<AgentInvocationResult> InvokeAsync(
        AgentInvocationConfiguration configuration,
        string? input,
        ResolvedAgentTools tools,
        CancellationToken cancellationToken = default,
        ToolExecutionContext? toolExecutionContext = null)
        => InvokeAsync(configuration, input, tools, observer: null, cancellationToken, toolExecutionContext);

    public async Task<AgentInvocationResult> InvokeAsync(
        AgentInvocationConfiguration configuration,
        string? input,
        ResolvedAgentTools tools,
        IInvocationObserver? observer,
        CancellationToken cancellationToken = default,
        ToolExecutionContext? toolExecutionContext = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tools);

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
            configuration.RetryContext,
            tools.GrantedSkills,
            configuration.DeclaredOutputs,
            configuration.ResolvedPartials));

        var toolAccessPolicy = MergeToolAccessPolicy(configuration.ToolAccessPolicy, tools, configuration);

        var toolRegistry = new ToolRegistry(BuildProviders(configuration, tools));
        var invocationLoop = new InvocationLoop(modelClient, toolRegistry, nowProvider);
        var loopResult = await invocationLoop.RunAsync(
            new InvocationLoopRequest(
                messages,
                configuration.Model,
                toolAccessPolicy,
                configuration.Budget,
                configuration.MaxTokens,
                configuration.Temperature,
                toolExecutionContext,
                configuration.DeclaredOutputs),
            observer,
            cancellationToken);

        activity?.SetTag(CodeFlowActivity.TagNames.DecisionKind, loopResult.Decision.PortName);
        if (string.Equals(loopResult.Decision.PortName, "Failed", StringComparison.Ordinal))
        {
            var reason = (loopResult.Decision.Payload as System.Text.Json.Nodes.JsonObject)?["reason"]?.GetValue<string>();
            activity?.SetTag(CodeFlowActivity.TagNames.FailureReason, reason);
            activity?.SetStatus(ActivityStatusCode.Error, reason);
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
            loopResult.ToolCallsExecuted,
            loopResult.ContextUpdates,
            loopResult.WorkflowUpdates);
    }

    private IEnumerable<IToolProvider> BuildProviders(
        AgentInvocationConfiguration configuration,
        ResolvedAgentTools tools)
    {
        if (tools.EnableHostTools)
        {
            yield return hostToolProvider;
        }

        // Sub-agents inherit the parent's resolved tool set (v1 semantics — no independent
        // role resolution per sub-agent).
        if (configuration.SubAgents is { Count: > 0 })
        {
            yield return new SubAgentToolProvider(this, configuration.SubAgents, tools);
        }

        if (tools.McpTools is { Count: > 0 })
        {
            if (mcpClient is null)
            {
                throw new InvalidOperationException("MCP tools were resolved, but no IMcpClient is registered.");
            }

            yield return new McpToolProvider(mcpClient, tools.McpTools);
        }
    }

    private static ToolAccessPolicy MergeToolAccessPolicy(
        ToolAccessPolicy? configured,
        ResolvedAgentTools tools,
        AgentInvocationConfiguration configuration)
    {
        if (tools.AllowedToolNames.Count == 0)
        {
            return configured ?? ToolAccessPolicy.AllowAll;
        }

        // spawn_subagent is a runtime-managed meta-tool; callers don't (and shouldn't) grant
        // it through a role. Implicitly allow it whenever SubAgents is configured.
        var allowed = new List<string>(tools.AllowedToolNames);
        if (configuration.SubAgents is { Count: > 0 })
        {
            allowed.Add(SubAgentToolProvider.SpawnToolName);
        }

        return new ToolAccessPolicy(
            AllowedToolNames: allowed,
            CategoryToolLimits: configured?.CategoryToolLimits);
    }
}
