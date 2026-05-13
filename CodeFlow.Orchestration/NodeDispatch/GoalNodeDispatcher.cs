using CodeFlow.Contracts;
using CodeFlow.Orchestration.TokenTracking;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Goal;
using CodeFlow.Runtime.Observability;
using CodeFlow.Runtime.Workspace;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Scriban.Runtime;
using System.Text;
using System.Text.Json;
using RuntimeToolExecutionContext = CodeFlow.Runtime.ToolExecutionContext;
using RuntimeToolRepositoryContext = CodeFlow.Runtime.ToolRepositoryContext;
using RuntimeToolWorkspaceContext = CodeFlow.Runtime.ToolWorkspaceContext;

namespace CodeFlow.Orchestration.NodeDispatch;

/// <summary>
/// Epic 978 / GN-3b — entry-point dispatcher for <see cref="WorkflowNodeKind.Goal"/>.
/// Unlike <see cref="AgentNodeDispatcher"/>, which publishes <see cref="AgentInvokeRequested"/>
/// to the bus and lets <see cref="AgentInvocationConsumer"/> drive the agent loop, the Goal
/// dispatcher runs <see cref="GoalIterationOrchestrator"/> SYNCHRONOUSLY in-process. That keeps
/// the cached LLM prefix warm across iterations (cross-process round-trips would invalidate it
/// because the consumer rebuilds the tool registry each invocation) and lets a mutable
/// <see cref="IGoalRuntimeState"/> instance see the model's <c>goal.update</c> call directly
/// — no serialisation of an interface across the message bus.
/// </summary>
/// <remarks>
/// On completion the dispatcher publishes a single synthetic <see cref="AgentInvocationCompleted"/>
/// with the outcome-derived port name, so the saga's existing routing
/// machinery (<c>RouteCompletionAsync</c>) handles Goal exits the same way it handles Agent exits.
/// <para/>
/// Deferred features (not in MVP; see sc-989 follow-ups):
/// <list type="bullet">
///   <item>Per-iteration token-usage observer wiring (tokens currently roll up in the final
///     <c>AgentInvocationCompleted.TokenUsage</c> only — no per-turn timeline events yet).</item>
///   <item>Authority snapshot / envelope resolution (no per-invocation envelope enforcement
///     for Goal invocations in v1).</item>
///   <item>Partial resolution + last-round-reminder injection (Goal nodes do not live inside
///     a ReviewLoop, and partials can be added in a follow-up if needed).</item>
///   <item>Retry-context threading.</item>
/// </list>
/// </remarks>
public sealed class GoalNodeDispatcher : IWorkflowNodeDispatcher
{
    /// <summary>Runtime default for <see cref="WorkflowNode.GoalMaxIterations"/> when the
    /// author leaves it null. Mirrors <c>WorkflowValidator.DefaultGoalMaxIterations</c>; kept
    /// here too because the API-layer validator is not reachable from the orchestration
    /// project.</summary>
    public const int DefaultGoalMaxIterations = 50;

    public WorkflowNodeKind Kind => WorkflowNodeKind.Goal;

    public async Task DispatchAsync(NodeDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var saga = request.Saga;
        var node = request.Node;
        var ctx = request.Context;
        var cancellationToken = ctx.CancellationToken;

        if (string.IsNullOrWhiteSpace(node.AgentKey))
        {
            throw new InvalidOperationException(
                $"Goal node {node.Id} has no AgentKey (epic 978 validator should have rejected this at save time).");
        }
        if (string.IsNullOrWhiteSpace(node.GoalObjective))
        {
            throw new InvalidOperationException(
                $"Goal node {node.Id} has no GoalObjective (epic 978 validator should have rejected this at save time).");
        }

        var services = ctx.GetPayload<IServiceProvider>();
        var agentConfigRepo = services.GetRequiredService<IAgentConfigRepository>();
        var roleResolution = services.GetRequiredService<IRoleResolutionService>();
        var renderer = services.GetRequiredService<IScribanTemplateRenderer>();
        var agentInvoker = services.GetRequiredService<IAgentInvoker>();
        var artifactStore = services.GetRequiredService<IArtifactStore>();
        // Token-usage observer is optional — production wires it, but legacy saga test
        // fixtures that construct the saga without a tokenUsageRecords repository skip it.
        // Mirrors AgentInvocationConsumer.BuildTokenUsageCaptureObserverAsync.
        var tokenUsageRecords = services.GetService<ITokenUsageRecordRepository>();
        var dbContext = services.GetRequiredService<CodeFlowDbContext>();

        // Pin the agent version on the saga — same pattern as PublishHandoffAsync.
        var pinnedVersion = saga.GetPinnedVersion(node.AgentKey);
        if (pinnedVersion is null)
        {
            pinnedVersion = node.AgentVersion
                ?? await agentConfigRepo.GetLatestVersionAsync(node.AgentKey, cancellationToken);
            saga.PinAgentVersion(node.AgentKey, pinnedVersion.Value);
        }

        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            var contextInputs = WorkflowSagaStateMachine.DeserializeContextInputs(saga.InputsJson);
            var workflowInputs = WorkflowSagaStateMachine.DeserializeContextInputs(saga.WorkflowInputsJson);

            // Render the Scriban objective against workflow + context vars so authors can
            // write `goalObjective: "Complete story {{ workflow.story_id }}"`. The
            // validator already proved the template parses; rendering errors here surface as
            // a Failed completion below.
            var renderedObjective = RenderGoalObjective(renderer, node.GoalObjective, contextInputs, workflowInputs);

            var agentConfig = await agentConfigRepo.GetAsync(node.AgentKey, pinnedVersion.Value, cancellationToken);

            var tools = await roleResolution.ResolveAsync(node.AgentKey, pinnedVersion.Value, cancellationToken);

            // Mirror AgentInvocationConsumer.Consume's variable-merge so the agent's prompt
            // template sees the same `context.*`, `workflow.*`, `input.*`, and budget vars on
            // every iteration. NOTE: we use the rendered objective as the input value so a
            // prompt-template that interpolates `{{ input }}` (like the existing dev-agent)
            // sees the objective text. The orchestrator separately injects the continuation
            // prompt as the user message starting on iteration 2.
            var baseConfig = agentConfig.Configuration with
            {
                Variables = AgentPromptScopeBuilder.Merge(
                    agentConfig.Configuration.Variables,
                    AgentPromptScopeBuilder.BuildContextVariables(contextInputs),
                    AgentPromptScopeBuilder.BuildWorkflowVariables(workflowInputs),
                    AgentPromptScopeBuilder.BuildBudgetVariables(agentConfig.Configuration.Budget),
                    AgentPromptScopeBuilder.BuildInputVariables(renderedObjective)),
                DeclaredOutputs = agentConfig.DeclaredOutputs.Count > 0
                    ? agentConfig.DeclaredOutputs
                    : null,
            };

            var toolExecutionContext = BuildGoalToolExecutionContext(saga);

            // Build the per-iteration token-usage observer. The orchestrator will pass it
            // through to every IAgentInvoker.InvokeAsync call so each iteration's
            // TokenUsageRecorded events fire individually — without this, the trace
            // inspector's per-node token chip shows only the final aggregate.
            var observer = await BuildTokenUsageObserverAsync(
                ctx, dbContext, tokenUsageRecords, saga.TraceId, node.Id, cancellationToken);

            var orchestrator = new GoalIterationOrchestrator(agentInvoker, renderer);
            var maxIterations = node.GoalMaxIterations ?? DefaultGoalMaxIterations;
            var goalRequest = new GoalIterationRequest(
                Objective: renderedObjective,
                TokenBudget: node.GoalTokenBudget,
                MaxIterations: maxIterations,
                BaseConfiguration: baseConfig,
                Tools: tools,
                ToolExecutionContext: toolExecutionContext,
                Observer: observer);

            var result = await orchestrator.RunAsync(goalRequest, cancellationToken);
            var duration = DateTimeOffset.UtcNow - startedAt;

            await PublishGoalCompletionAsync(ctx, saga, node, pinnedVersion.Value, result, duration, artifactStore);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var duration = DateTimeOffset.UtcNow - startedAt;
            await PublishGoalFailureAsync(ctx, saga, node, pinnedVersion.Value, ex, duration, artifactStore);
        }
    }

    /// <summary>
    /// Construct the per-iteration token-usage observer. Returns null when
    /// <paramref name="tokenUsageRecords"/> is null — legacy saga test fixtures construct the
    /// saga without a repository, and the orchestrator's observer overload tolerates a null.
    /// Mirrors <c>AgentInvocationConsumer.BuildTokenUsageCaptureObserverAsync</c> so a Goal
    /// invocation's token events land in the same trace stream as a regular Agent invocation,
    /// attributed to the same NodeId regardless of iteration count.
    /// </summary>
    internal static async Task<IInvocationObserver?> BuildTokenUsageObserverAsync(
        IPublishEndpoint publishEndpoint,
        CodeFlowDbContext dbContext,
        ITokenUsageRecordRepository? tokenUsageRecords,
        Guid traceId,
        Guid nodeId,
        CancellationToken cancellationToken)
    {
        if (tokenUsageRecords is null)
        {
            return null;
        }
        var scope = await SagaScopeChainResolver.ResolveAsync(dbContext, traceId, cancellationToken);
        return new TokenUsageCaptureObserver(
            tokenUsageRecords,
            rootTraceId: scope.RootTraceId,
            nodeId: nodeId,
            scopeChain: scope.ScopeChain,
            publishEndpoint: publishEndpoint);
    }

    internal static string RenderGoalObjective(
        IScribanTemplateRenderer renderer,
        string template,
        IReadOnlyDictionary<string, JsonElement> contextInputs,
        IReadOnlyDictionary<string, JsonElement> workflowInputs)
    {
        var scope = new ScriptObject();
        scope.SetValue("workflow", ToScriptObject(workflowInputs), readOnly: true);
        scope.SetValue("context", ToScriptObject(contextInputs), readOnly: true);
        return renderer.Render(template, scope);
    }

    private static ScriptObject ToScriptObject(IReadOnlyDictionary<string, JsonElement> values)
    {
        var result = new ScriptObject();
        foreach (var kv in values)
        {
            result[kv.Key] = ConvertJsonToScribanValue(kv.Value);
        }
        return result;
    }

    private static object? ConvertJsonToScribanValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var i) => i,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => ToScriptObject(element),
        JsonValueKind.Array => ToScriptArray(element),
        _ => element.GetRawText(),
    };

    private static ScriptObject ToScriptObject(JsonElement element)
    {
        var result = new ScriptObject();
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ConvertJsonToScribanValue(property.Value);
        }
        return result;
    }

    private static ScriptArray ToScriptArray(JsonElement element)
    {
        var result = new ScriptArray();
        foreach (var item in element.EnumerateArray())
        {
            result.Add(ConvertJsonToScribanValue(item));
        }
        return result;
    }

    /// <summary>
    /// Build the runtime tool-execution context from saga state. MVP scope: workspace anchor
    /// (TraceWorkDir) + per-trace repositories. No legacy contract-shape fallback (Goal nodes
    /// only run inside code-aware workflows that set TraceWorkDir) and no envelope (deferred
    /// to a follow-up — see class-level remarks).
    /// </summary>
    internal static RuntimeToolExecutionContext? BuildGoalToolExecutionContext(WorkflowSagaStateEntity saga)
    {
        if (string.IsNullOrWhiteSpace(saga.TraceWorkDir))
        {
            return null;
        }

        var rootTraceId = AgentInvocationConsumer.TryParseRootTraceId(saga.TraceWorkDir);
        var repositoryContexts = ResolveRepositoryContexts(saga);
        return new RuntimeToolExecutionContext(
            new RuntimeToolWorkspaceContext(saga.TraceId, saga.TraceWorkDir, rootTraceId),
            repositoryContexts,
            Envelope: null);
    }

    private static IReadOnlyList<RuntimeToolRepositoryContext>? ResolveRepositoryContexts(WorkflowSagaStateEntity saga)
    {
        var sagaRepos = WorkflowSagaStateMachine.ParseRepositoriesJson(saga.RepositoriesJson);
        if (sagaRepos is not { Count: > 0 })
        {
            return null;
        }

        var result = new List<RuntimeToolRepositoryContext>(sagaRepos.Count);
        foreach (var entry in sagaRepos)
        {
            if (string.IsNullOrWhiteSpace(entry.Url))
            {
                continue;
            }
            try
            {
                var repo = RepoReference.Parse(entry.Url);
                result.Add(new RuntimeToolRepositoryContext(repo.Owner, repo.Name, entry.Url, repo.IdentityKey, repo.Slug));
            }
            catch (ArgumentException)
            {
                // Malformed entries are skipped — same behaviour as
                // AgentInvocationConsumer.ResolveRepositoryContexts.
            }
        }
        return result.Count > 0 ? result : null;
    }

    private static async Task PublishGoalCompletionAsync(
        BehaviorContext<WorkflowSagaStateEntity> ctx,
        WorkflowSagaStateEntity saga,
        WorkflowNode node,
        int agentVersion,
        GoalIterationResult result,
        TimeSpan duration,
        IArtifactStore artifactStore)
    {
        var (portName, output) = MapOutcome(result);
        var outputRef = await WriteOutputArtifactAsync(
            artifactStore,
            saga,
            $"goal-{node.Id:N}-output.txt",
            output,
            ctx.CancellationToken);

        await ctx.Publish(new AgentInvocationCompleted(
            TraceId: saga.TraceId,
            RoundId: saga.CurrentRoundId,
            FromNodeId: node.Id,
            AgentKey: node.AgentKey!,
            AgentVersion: agentVersion,
            OutputPortName: portName,
            OutputRef: outputRef,
            DecisionPayload: BuildDecisionPayload(result),
            Duration: duration,
            TokenUsage: new CodeFlow.Contracts.TokenUsage(
                InputTokens: result.TotalInputTokens,
                OutputTokens: result.TotalOutputTokens,
                TotalTokens: result.TotalInputTokens + result.TotalOutputTokens),
            ContextUpdates: null,
            WorkflowUpdates: null),
            ctx.CancellationToken);
    }

    private static async Task PublishGoalFailureAsync(
        BehaviorContext<WorkflowSagaStateEntity> ctx,
        WorkflowSagaStateEntity saga,
        WorkflowNode node,
        int agentVersion,
        Exception ex,
        TimeSpan duration,
        IArtifactStore artifactStore)
    {
        var outputRef = await WriteOutputArtifactAsync(
            artifactStore,
            saga,
            $"goal-{node.Id:N}-error.txt",
            $"Goal node failed: {ex.Message}",
            ctx.CancellationToken);

        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["reason"] = "GoalInvocationFailed",
            ["message"] = ex.Message,
            ["exceptionType"] = ex.GetType().FullName,
        };

        await ctx.Publish(new AgentInvocationCompleted(
            TraceId: saga.TraceId,
            RoundId: saga.CurrentRoundId,
            FromNodeId: node.Id,
            AgentKey: node.AgentKey!,
            AgentVersion: agentVersion,
            OutputPortName: "Failed",
            OutputRef: outputRef,
            DecisionPayload: JsonDocument.Parse(payload.ToJsonString()).RootElement.Clone(),
            Duration: duration,
            TokenUsage: new CodeFlow.Contracts.TokenUsage(0, 0, 0),
            ContextUpdates: null,
            WorkflowUpdates: null),
            ctx.CancellationToken);
    }

    internal static (string PortName, string Output) MapOutcome(GoalIterationResult result)
    {
        var lastOutput = result.Iterations.Count > 0
            ? result.Iterations[^1].Output
            : string.Empty;
        return result.Outcome switch
        {
            GoalIterationOutcome.Success => ("Success", lastOutput),
            GoalIterationOutcome.BudgetLimited => ("BudgetLimited", lastOutput),
            GoalIterationOutcome.Abandoned => ("Abandoned", lastOutput),
            GoalIterationOutcome.IterationCapReached => ("Failed", lastOutput),
            _ => ("Failed", lastOutput),
        };
    }

    internal static JsonElement? BuildDecisionPayload(GoalIterationResult result)
    {
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["outcome"] = result.Outcome.ToString(),
            ["iterationCount"] = result.Iterations.Count,
            ["tokensUsed"] = result.FinalSnapshot.TokensUsed,
            ["tokenBudget"] = result.FinalSnapshot.TokenBudget,
            ["isCompleteRequested"] = result.FinalSnapshot.IsCompleteRequested,
        };
        if (result.Outcome == GoalIterationOutcome.IterationCapReached)
        {
            payload["reason"] = "GoalIterationCapReached";
        }
        if (result.Outcome == GoalIterationOutcome.Abandoned)
        {
            // The reason is the load-bearing field for the Abandoned port — downstream
            // postmortem / HITL handlers route on it. Preserve verbatim so the agent's
            // language survives serialization.
            payload["reason"] = "GoalAbandoned";
            payload["abandonReason"] = result.FinalSnapshot.AbandonReason;
        }
        return JsonDocument.Parse(payload.ToJsonString()).RootElement.Clone();
    }

    private static async Task<Uri> WriteOutputArtifactAsync(
        IArtifactStore artifactStore,
        WorkflowSagaStateEntity saga,
        string fileName,
        string content,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await artifactStore.WriteAsync(
            stream,
            new ArtifactMetadata(
                saga.TraceId,
                saga.CurrentRoundId,
                Guid.NewGuid(),
                ContentType: "text/plain",
                FileName: fileName),
            cancellationToken);
    }
}
