using CodeFlow.Contracts;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Observability;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;

namespace CodeFlow.Orchestration;

public sealed class WorkflowSagaStateMachine : MassTransitStateMachine<WorkflowSagaStateEntity>
{
    public const string PendingTransitionCompleted = "Completed";
    public const string PendingTransitionFailed = "Failed";
    public const string PendingTransitionEscalated = "Escalated";

    private const int MaxLogicChainHops = 32;

    public State Running { get; } = null!;

    public State Completed { get; } = null!;

    public State Failed { get; } = null!;

    public State Escalated { get; } = null!;

    public Event<AgentInvokeRequested> AgentInvokeRequestedEvent { get; } = null!;

    public Event<AgentInvocationCompleted> AgentInvocationCompletedEvent { get; } = null!;

    public WorkflowSagaStateMachine()
    {
        InstanceState(saga => saga.CurrentState);

        Event(() => AgentInvokeRequestedEvent, config =>
        {
            config.CorrelateById(context => context.Message.TraceId);
        });

        Event(() => AgentInvocationCompletedEvent, config =>
        {
            config.CorrelateById(context => context.Message.TraceId);
        });

        Initially(
            When(AgentInvokeRequestedEvent)
                .Then(context => ApplyInitialRequest(context.Saga, context.Message))
                .TransitionTo(Running));

        DuringAny(Ignore(AgentInvokeRequestedEvent));

        During(Completed, Ignore(AgentInvocationCompletedEvent));
        During(Failed, Ignore(AgentInvocationCompletedEvent));
        During(Escalated, Ignore(AgentInvocationCompletedEvent));

        During(Running,
            When(AgentInvocationCompletedEvent)
                .ThenAsync(context => RouteCompletionAsync(context))
                .IfElse(
                    context => context.Saga.PendingTransition == PendingTransitionCompleted,
                    completeBinder => completeBinder
                        .Then(ClearPendingTransition)
                        .TransitionTo(Completed),
                    continueBinder => continueBinder
                        .IfElse(
                            context => context.Saga.PendingTransition == PendingTransitionFailed,
                            failBinder => failBinder
                                .Then(ClearPendingTransition)
                                .TransitionTo(Failed),
                            continueElseBinder => continueElseBinder
                                .If(
                                    context => context.Saga.PendingTransition == PendingTransitionEscalated,
                                    escalateBinder => escalateBinder
                                        .Then(ClearPendingTransition)
                                        .TransitionTo(Escalated)))));
    }

    private static void ApplyInitialRequest(WorkflowSagaStateEntity saga, AgentInvokeRequested message)
    {
        saga.TraceId = message.TraceId;
        saga.WorkflowKey = message.WorkflowKey;
        saga.WorkflowVersion = message.WorkflowVersion;
        saga.CurrentNodeId = message.NodeId;
        saga.CurrentAgentKey = message.AgentKey;
        saga.CurrentRoundId = message.RoundId;
        saga.RoundCount = 0;
        saga.InputsJson = SerializeContextInputs(message.ContextInputs);
        saga.PinAgentVersion(message.AgentKey, message.AgentVersion);
        saga.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static void ClearPendingTransition(BehaviorContext<WorkflowSagaStateEntity> context)
    {
        context.Saga.PendingTransition = null;
    }

    private static async Task RouteCompletionAsync(
        BehaviorContext<WorkflowSagaStateEntity, AgentInvocationCompleted> context)
    {
        var saga = context.Saga;
        var message = context.Message;

        using var activity = CodeFlowActivity.StartWorkflowRoot(
            "workflow.saga.route",
            saga.TraceId);
        activity?.SetTag(CodeFlowActivity.TagNames.RoundId, saga.CurrentRoundId);
        activity?.SetTag(CodeFlowActivity.TagNames.WorkflowKey, saga.WorkflowKey);
        activity?.SetTag(CodeFlowActivity.TagNames.WorkflowVersion, saga.WorkflowVersion);
        activity?.SetTag(CodeFlowActivity.TagNames.AgentKey, message.AgentKey);
        activity?.SetTag(CodeFlowActivity.TagNames.DecisionKind, message.Decision.ToString());

        var services = context.GetPayload<IServiceProvider>();
        var workflowRepo = services.GetRequiredService<IWorkflowRepository>();
        var agentConfigRepo = services.GetRequiredService<IAgentConfigRepository>();
        var artifactStore = services.GetRequiredService<IArtifactStore>();
        var scriptHost = services.GetRequiredService<LogicNodeScriptHost>();

        var runtimeKind = MapDecisionKind(message.Decision);

        saga.AppendDecision(new DecisionRecord(
            AgentKey: message.AgentKey,
            AgentVersion: message.AgentVersion,
            Decision: runtimeKind,
            DecisionPayload: CloneDecisionPayload(message.DecisionPayload),
            RoundId: saga.CurrentRoundId,
            RecordedAtUtc: DateTime.UtcNow));

        var workflow = await workflowRepo.GetAsync(
            saga.WorkflowKey,
            saga.WorkflowVersion,
            context.CancellationToken);

        if (saga.EscalatedFromNodeId is Guid escalatedFromNodeId)
        {
            await HandleEscalationResponseAsync(
                context,
                agentConfigRepo,
                workflow,
                saga,
                message,
                escalatedFromNodeId,
                runtimeKind);
            saga.UpdatedAtUtc = DateTime.UtcNow;
            activity?.SetTag(CodeFlowActivity.TagNames.SagaState, saga.PendingTransition ?? "EscalationRecovered");
            return;
        }

        var edge = workflow.FindNext(message.FromNodeId, message.OutputPortName);

        if (edge is null)
        {
            saga.PendingTransition = message.Decision == AgentDecisionKind.Completed
                ? PendingTransitionCompleted
                : PendingTransitionFailed;

            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        var resolution = await ResolveTargetThroughLogicChainAsync(
            context,
            workflow,
            saga,
            scriptHost,
            artifactStore,
            edge,
            upstreamOutputRef: message.OutputRef);

        if (resolution is { FailureTerminal: true })
        {
            saga.PendingTransition = PendingTransitionFailed;
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        if (resolution is null)
        {
            throw new InvalidOperationException("Logic chain resolver returned no outcome.");
        }

        var targetNode = resolution.TerminalNode!;
        var targetRoundId = resolution.RotatesRound ? Guid.NewGuid() : saga.CurrentRoundId;
        var targetRoundCount = resolution.RotatesRound ? 0 : saga.RoundCount + 1;
        var retryContext = BuildRetryContextForHandoff(saga, message);

        if (!resolution.RotatesRound && targetRoundCount >= workflow.MaxRoundsPerRound)
        {
            var escalationNode = workflow.EscalationNode;
            if (escalationNode is not null)
            {
                await PublishHandoffAsync(
                    context,
                    agentConfigRepo,
                    saga,
                    workflow,
                    escalationNode,
                    inputRef: message.OutputRef,
                    roundId: saga.CurrentRoundId,
                    retryContext: retryContext);

                saga.EscalatedFromNodeId = message.FromNodeId;
                saga.CurrentNodeId = escalationNode.Id;
                saga.CurrentAgentKey = escalationNode.AgentKey ?? string.Empty;
            }
            else
            {
                saga.PendingTransition = PendingTransitionFailed;
            }

            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        await DispatchToNodeAsync(
            context,
            agentConfigRepo,
            saga,
            workflow,
            targetNode,
            inputRef: message.OutputRef,
            roundId: targetRoundId,
            retryContext: retryContext);

        saga.CurrentNodeId = targetNode.Id;
        saga.CurrentAgentKey = targetNode.AgentKey ?? string.Empty;
        saga.CurrentRoundId = targetRoundId;
        saga.RoundCount = targetRoundCount;
        saga.UpdatedAtUtc = DateTime.UtcNow;

        activity?.SetTag(CodeFlowActivity.TagNames.SagaState, saga.PendingTransition ?? "Routed");
    }

    private static async Task<LogicChainResolution?> ResolveTargetThroughLogicChainAsync(
        BehaviorContext<WorkflowSagaStateEntity, AgentInvocationCompleted> context,
        Workflow workflow,
        WorkflowSagaStateEntity saga,
        LogicNodeScriptHost scriptHost,
        IArtifactStore artifactStore,
        WorkflowEdge initialEdge,
        Uri upstreamOutputRef)
    {
        var rotates = initialEdge.RotatesRound;
        var currentNode = workflow.FindNode(initialEdge.ToNodeId)
            ?? throw new InvalidOperationException(
                $"Edge {initialEdge.FromNodeId}:{initialEdge.FromPort} → {initialEdge.ToNodeId} references a missing node.");

        if (currentNode.Kind != WorkflowNodeKind.Logic)
        {
            return new LogicChainResolution(currentNode, rotates, FailureTerminal: false);
        }

        JsonElement inputJson = default;
        var inputLoaded = false;
        var contextInputs = DeserializeContextInputs(saga.InputsJson);

        for (var hops = 0; hops < MaxLogicChainHops; hops++)
        {
            if (!inputLoaded)
            {
                inputJson = await ReadArtifactAsJsonAsync(artifactStore, upstreamOutputRef, context.CancellationToken);
                inputLoaded = true;
            }

            if (string.IsNullOrWhiteSpace(currentNode.Script))
            {
                saga.AppendLogicEvaluation(LogicEvaluationRecordFailure(
                    currentNode.Id,
                    saga.CurrentRoundId,
                    TimeSpan.Zero,
                    Array.Empty<string>(),
                    kind: "ConfigurationError",
                    message: $"Logic node {currentNode.Id} has no script."));
                return new LogicChainResolution(null, rotates, FailureTerminal: true);
            }

            var eval = scriptHost.Evaluate(
                workflowKey: workflow.Key,
                workflowVersion: workflow.Version,
                nodeId: currentNode.Id,
                script: currentNode.Script,
                declaredPorts: currentNode.OutputPorts,
                input: inputJson,
                context: contextInputs,
                cancellationToken: context.CancellationToken);

            saga.AppendLogicEvaluation(new LogicEvaluationRecord(
                NodeId: currentNode.Id,
                OutputPortName: eval.OutputPortName,
                RoundId: saga.CurrentRoundId,
                Duration: eval.Duration,
                Logs: eval.LogEntries,
                FailureKind: eval.Failure?.ToString(),
                FailureMessage: eval.FailureMessage,
                RecordedAtUtc: DateTime.UtcNow));

            var chosenPort = eval.IsSuccess
                ? eval.OutputPortName!
                : AgentDecisionPorts.FailedPort;

            var nextEdge = workflow.FindNext(currentNode.Id, chosenPort);
            if (nextEdge is null)
            {
                return new LogicChainResolution(null, rotates, FailureTerminal: true);
            }

            if (nextEdge.RotatesRound)
            {
                rotates = true;
            }

            currentNode = workflow.FindNode(nextEdge.ToNodeId)
                ?? throw new InvalidOperationException(
                    $"Edge {nextEdge.FromNodeId}:{nextEdge.FromPort} → {nextEdge.ToNodeId} references a missing node.");

            if (currentNode.Kind != WorkflowNodeKind.Logic)
            {
                return new LogicChainResolution(currentNode, rotates, FailureTerminal: false);
            }
        }

        saga.AppendLogicEvaluation(LogicEvaluationRecordFailure(
            currentNode.Id,
            saga.CurrentRoundId,
            TimeSpan.Zero,
            Array.Empty<string>(),
            kind: "LogicChainTooLong",
            message: $"Logic chain exceeded {MaxLogicChainHops} hops."));
        return new LogicChainResolution(null, rotates, FailureTerminal: true);
    }

    private static LogicEvaluationRecord LogicEvaluationRecordFailure(
        Guid nodeId,
        Guid roundId,
        TimeSpan duration,
        IReadOnlyList<string> logs,
        string kind,
        string message) => new(
            NodeId: nodeId,
            OutputPortName: null,
            RoundId: roundId,
            Duration: duration,
            Logs: logs,
            FailureKind: kind,
            FailureMessage: message,
            RecordedAtUtc: DateTime.UtcNow);

    private sealed record LogicChainResolution(
        WorkflowNode? TerminalNode,
        bool RotatesRound,
        bool FailureTerminal);

    private static async Task<JsonElement> ReadArtifactAsJsonAsync(
        IArtifactStore artifactStore,
        Uri outputRef,
        CancellationToken cancellationToken)
    {
        await using var stream = await artifactStore.ReadAsync(outputRef, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
        var text = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            // Upstream agent produced plain text — expose as { "text": "…" } so scripts can still read it.
            var doc = new { text };
            return JsonSerializer.SerializeToElement(doc);
        }
    }

    private static async Task HandleEscalationResponseAsync(
        BehaviorContext<WorkflowSagaStateEntity, AgentInvocationCompleted> context,
        IAgentConfigRepository agentConfigRepo,
        Workflow workflow,
        WorkflowSagaStateEntity saga,
        AgentInvocationCompleted message,
        Guid resumeFromNodeId,
        Runtime.AgentDecisionKind decision)
    {
        saga.EscalatedFromNodeId = null;

        switch (decision)
        {
            case Runtime.AgentDecisionKind.Approved:
            case Runtime.AgentDecisionKind.ApprovedWithActions:
                var resumeNode = workflow.FindNode(resumeFromNodeId)
                    ?? throw new InvalidOperationException(
                        $"Escalation recovery target node {resumeFromNodeId} is missing from workflow {workflow.Key} v{workflow.Version}.");

                var recoveryRoundId = Guid.NewGuid();
                await DispatchToNodeAsync(
                    context,
                    agentConfigRepo,
                    saga,
                    workflow,
                    resumeNode,
                    inputRef: message.OutputRef,
                    roundId: recoveryRoundId,
                    retryContext: null);

                saga.CurrentNodeId = resumeNode.Id;
                saga.CurrentAgentKey = resumeNode.AgentKey ?? string.Empty;
                saga.CurrentRoundId = recoveryRoundId;
                saga.RoundCount = 0;
                return;

            case Runtime.AgentDecisionKind.Completed:
                saga.PendingTransition = PendingTransitionCompleted;
                return;

            case Runtime.AgentDecisionKind.Rejected:
                saga.PendingTransition = PendingTransitionEscalated;
                return;

            case Runtime.AgentDecisionKind.Failed:
            default:
                saga.PendingTransition = PendingTransitionFailed;
                return;
        }
    }

    private static Task DispatchToNodeAsync(
        BehaviorContext<WorkflowSagaStateEntity, AgentInvocationCompleted> context,
        IAgentConfigRepository agentConfigRepo,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode node,
        Uri inputRef,
        Guid roundId,
        CodeFlow.Contracts.RetryContext? retryContext)
    {
        return node.Kind switch
        {
            WorkflowNodeKind.Agent or WorkflowNodeKind.Hitl or WorkflowNodeKind.Escalation or WorkflowNodeKind.Start =>
                PublishHandoffAsync(context, agentConfigRepo, saga, workflow, node, inputRef, roundId, retryContext),
            WorkflowNodeKind.Logic =>
                throw new InvalidOperationException(
                    "Logic nodes should have been resolved by the logic chain resolver before reaching DispatchToNodeAsync."),
            _ =>
                throw new InvalidOperationException($"Unknown workflow node kind: {node.Kind}.")
        };
    }

    private static async Task PublishHandoffAsync(
        BehaviorContext<WorkflowSagaStateEntity, AgentInvocationCompleted> context,
        IAgentConfigRepository agentConfigRepo,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode targetNode,
        Uri inputRef,
        Guid roundId,
        CodeFlow.Contracts.RetryContext? retryContext)
    {
        if (string.IsNullOrWhiteSpace(targetNode.AgentKey))
        {
            throw new InvalidOperationException(
                $"Node {targetNode.Id} of kind {targetNode.Kind} in workflow {workflow.Key} v{workflow.Version} has no AgentKey.");
        }

        var targetAgentKey = targetNode.AgentKey;
        var pinnedVersion = saga.GetPinnedVersion(targetAgentKey);

        if (pinnedVersion is null)
        {
            pinnedVersion = targetNode.AgentVersion
                ?? await agentConfigRepo.GetLatestVersionAsync(
                    targetAgentKey,
                    context.CancellationToken);

            saga.PinAgentVersion(targetAgentKey, pinnedVersion.Value);
        }

        await context.Publish(new AgentInvokeRequested(
            TraceId: saga.TraceId,
            RoundId: roundId,
            WorkflowKey: saga.WorkflowKey,
            WorkflowVersion: saga.WorkflowVersion,
            NodeId: targetNode.Id,
            AgentKey: targetAgentKey,
            AgentVersion: pinnedVersion.Value,
            InputRef: inputRef,
            ContextInputs: DeserializeContextInputs(saga.InputsJson),
            RetryContext: retryContext));
    }

    private static CodeFlow.Contracts.RetryContext? BuildRetryContextForHandoff(
        WorkflowSagaStateEntity saga,
        AgentInvocationCompleted message)
    {
        if (message.Decision != AgentDecisionKind.Failed)
        {
            return null;
        }

        var attemptNumber = CountPriorFailedAttempts(saga) + 1;
        var (reason, summary) = ExtractFailureContext(message.DecisionPayload);

        return new CodeFlow.Contracts.RetryContext(
            AttemptNumber: attemptNumber,
            PriorFailureReason: reason,
            PriorAttemptSummary: summary);
    }

    private static int CountPriorFailedAttempts(WorkflowSagaStateEntity saga)
    {
        var history = saga.GetDecisionHistory();
        return history.Count(record =>
            record.RoundId == saga.CurrentRoundId
            && record.Decision == Runtime.AgentDecisionKind.Failed);
    }

    private static (string? Reason, string? Summary) ExtractFailureContext(JsonElement? payload)
    {
        if (payload is null || payload.Value.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        string? reason = null;
        if (payload.Value.TryGetProperty("reason", out var reasonProperty)
            && reasonProperty.ValueKind == JsonValueKind.String)
        {
            reason = reasonProperty.GetString();
        }

        if (!payload.Value.TryGetProperty("failure_context", out var failureContext)
            || failureContext.ValueKind != JsonValueKind.Object)
        {
            return (reason, null);
        }

        string? lastOutput = null;
        if (failureContext.TryGetProperty("last_output", out var lastOutputProperty)
            && lastOutputProperty.ValueKind == JsonValueKind.String)
        {
            lastOutput = lastOutputProperty.GetString();
        }

        int? toolCallsExecuted = null;
        if (failureContext.TryGetProperty("tool_calls_executed", out var toolCallsProperty)
            && toolCallsProperty.ValueKind == JsonValueKind.Number
            && toolCallsProperty.TryGetInt32(out var toolCalls))
        {
            toolCallsExecuted = toolCalls;
        }

        var summaryBuilder = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(lastOutput))
        {
            summaryBuilder.Append("Last output: ").Append(lastOutput!.Trim());
        }

        if (toolCallsExecuted is { } calls)
        {
            if (summaryBuilder.Length > 0)
            {
                summaryBuilder.Append(Environment.NewLine);
            }

            summaryBuilder.Append("Tool calls executed: ").Append(calls);
        }

        var summary = summaryBuilder.Length == 0 ? null : summaryBuilder.ToString();
        return (reason, summary);
    }

    private static Runtime.AgentDecisionKind MapDecisionKind(AgentDecisionKind kind)
    {
        return kind switch
        {
            AgentDecisionKind.Completed => Runtime.AgentDecisionKind.Completed,
            AgentDecisionKind.Approved => Runtime.AgentDecisionKind.Approved,
            AgentDecisionKind.ApprovedWithActions => Runtime.AgentDecisionKind.ApprovedWithActions,
            AgentDecisionKind.Rejected => Runtime.AgentDecisionKind.Rejected,
            AgentDecisionKind.Failed => Runtime.AgentDecisionKind.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported decision kind.")
        };
    }

    private static JsonElement? CloneDecisionPayload(JsonElement? payload)
    {
        return payload?.Clone();
    }

    private static string SerializeContextInputs(IReadOnlyDictionary<string, JsonElement> inputs)
    {
        if (inputs.Count == 0)
        {
            return "{}";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in inputs)
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IReadOnlyDictionary<string, JsonElement> DeserializeContextInputs(string? inputsJson)
    {
        if (string.IsNullOrWhiteSpace(inputsJson))
        {
            return EmptyInputs;
        }

        using var document = JsonDocument.Parse(inputsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return EmptyInputs;
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyInputs =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}
