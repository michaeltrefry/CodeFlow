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

    /// <summary>
    /// Cap on subflow nesting depth. Top-level workflow runs at depth 0; a Subflow node spawns a
    /// child at <c>parent.SubflowDepth + 1</c>. A child whose depth would exceed this value is
    /// not spawned — the parent saga fails fast with reason <c>SubflowDepthExceeded</c>.
    /// </summary>
    public const int MaxSubflowDepth = 3;

    public State Running { get; } = null!;

    public State Completed { get; } = null!;

    public State Failed { get; } = null!;

    public State Escalated { get; } = null!;

    public Event<AgentInvokeRequested> AgentInvokeRequestedEvent { get; } = null!;

    public Event<AgentInvocationCompleted> AgentInvocationCompletedEvent { get; } = null!;

    public Event<SubflowInvokeRequested> SubflowInvokeRequestedEvent { get; } = null!;

    public Event<SubflowCompleted> SubflowCompletedEvent { get; } = null!;

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

        Event(() => SubflowInvokeRequestedEvent, config =>
        {
            // Child saga's CorrelationId is the ChildTraceId carried on the message.
            config.CorrelateById(context => context.Message.ChildTraceId);
        });

        Event(() => SubflowCompletedEvent, config =>
        {
            // Routed back to the parent saga, which is correlated by ParentTraceId.
            config.CorrelateById(context => context.Message.ParentTraceId);
        });

        Initially(
            When(AgentInvokeRequestedEvent)
                .Then(context => ApplyInitialRequest(context.Saga, context.Message))
                .TransitionTo(Running),
            When(SubflowInvokeRequestedEvent)
                .ThenAsync(context => ApplyInitialSubflowAsync(context))
                .TransitionTo(Running));

        DuringAny(Ignore(AgentInvokeRequestedEvent));
        DuringAny(Ignore(SubflowInvokeRequestedEvent));

        During(Completed, Ignore(AgentInvocationCompletedEvent), Ignore(SubflowCompletedEvent));
        During(Failed, Ignore(AgentInvocationCompletedEvent), Ignore(SubflowCompletedEvent));
        During(Escalated, Ignore(AgentInvocationCompletedEvent), Ignore(SubflowCompletedEvent));

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
                                        .TransitionTo(Escalated)))),
            When(SubflowCompletedEvent)
                .ThenAsync(context => RouteSubflowCompletionAsync(context))
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

        WhenEnter(Completed, binder => binder.ThenAsync(context => PublishSubflowCompletedIfChildAsync(context, "Completed")));
        WhenEnter(Failed, binder => binder.ThenAsync(context => PublishSubflowCompletedIfChildAsync(context, "Failed")));
        WhenEnter(Escalated, binder => binder.ThenAsync(context => PublishSubflowCompletedIfChildAsync(context, "Escalated")));
    }

    private static void ApplyInitialRequest(WorkflowSagaStateEntity saga, AgentInvokeRequested message)
    {
        var nowUtc = DateTime.UtcNow;
        saga.TraceId = message.TraceId;
        saga.WorkflowKey = message.WorkflowKey;
        saga.WorkflowVersion = message.WorkflowVersion;
        saga.CurrentNodeId = message.NodeId;
        saga.CurrentAgentKey = message.AgentKey;
        saga.CurrentRoundId = message.RoundId;
        saga.RoundCount = 0;
        saga.InputsJson = SerializeContextInputs(message.ContextInputs);
        saga.CurrentInputRef = message.InputRef?.ToString();
        saga.PinAgentVersion(message.AgentKey, message.AgentVersion);
        if (saga.CreatedAtUtc == default)
        {
            saga.CreatedAtUtc = nowUtc;
        }
        saga.UpdatedAtUtc = nowUtc;
    }

    private static void ClearPendingTransition(BehaviorContext<WorkflowSagaStateEntity> context)
    {
        context.Saga.PendingTransition = null;
    }

    /// <summary>
    /// Initialize a child saga from a <see cref="SubflowInvokeRequested"/>. Loads the child
    /// workflow, copies parent linkage + global snapshot onto the saga, pins the child Start's
    /// agent version, and publishes an <see cref="AgentInvokeRequested"/> for the child Start
    /// node so the existing agent invocation pipeline picks it up. The published
    /// AgentInvokeRequested is correlated by <c>ChildTraceId</c> — the saga is already in
    /// <c>Running</c> by the time it arrives, so it falls into the <c>DuringAny(Ignore(...))</c>
    /// guard rather than re-running the initial transition.
    /// </summary>
    private static async Task ApplyInitialSubflowAsync(
        BehaviorContext<WorkflowSagaStateEntity, SubflowInvokeRequested> context)
    {
        var saga = context.Saga;
        var message = context.Message;

        var services = context.GetPayload<IServiceProvider>();
        var workflowRepo = services.GetRequiredService<IWorkflowRepository>();
        var agentConfigRepo = services.GetRequiredService<IAgentConfigRepository>();

        var workflow = await workflowRepo.GetAsync(
            message.SubflowKey,
            message.SubflowVersion,
            context.CancellationToken);

        var startNode = workflow.StartNode;
        if (string.IsNullOrWhiteSpace(startNode.AgentKey))
        {
            throw new InvalidOperationException(
                $"Subflow {workflow.Key} v{workflow.Version} has no AgentKey on its Start node.");
        }

        var nowUtc = DateTime.UtcNow;
        var childRoundId = Guid.NewGuid();

        saga.TraceId = message.ChildTraceId;
        saga.WorkflowKey = message.SubflowKey;
        saga.WorkflowVersion = message.SubflowVersion;
        saga.CurrentNodeId = startNode.Id;
        saga.CurrentAgentKey = startNode.AgentKey;
        saga.CurrentRoundId = childRoundId;
        saga.RoundCount = 0;
        saga.InputsJson = "{}";
        saga.GlobalInputsJson = SerializeContextInputs(message.SharedContext);
        saga.CurrentInputRef = message.InputRef.ToString();
        saga.ParentTraceId = message.ParentTraceId;
        saga.ParentNodeId = message.ParentNodeId;
        saga.ParentRoundId = message.ParentRoundId;
        saga.SubflowDepth = message.Depth;
        saga.ParentReviewRound = message.ReviewRound;
        saga.ParentReviewMaxRounds = message.ReviewMaxRounds;
        saga.ParentLoopDecision = message.LoopDecision;
        if (saga.CreatedAtUtc == default)
        {
            saga.CreatedAtUtc = nowUtc;
        }
        saga.UpdatedAtUtc = nowUtc;

        var startAgentVersion = startNode.AgentVersion
            ?? await agentConfigRepo.GetLatestVersionAsync(startNode.AgentKey, context.CancellationToken);
        saga.PinAgentVersion(startNode.AgentKey, startAgentVersion);

        // The child's local `context` starts empty — the parent's shared snapshot belongs on
        // `global`, not on the child's local inputs. Publishing SharedContext as ContextInputs
        // would make parent keys show up under {{context.*}} on the child Start, and
        // {{global.*}} would be empty — the opposite of the documented semantics.
        await context.Publish(new AgentInvokeRequested(
            TraceId: message.ChildTraceId,
            RoundId: childRoundId,
            WorkflowKey: workflow.Key,
            WorkflowVersion: workflow.Version,
            NodeId: startNode.Id,
            AgentKey: startNode.AgentKey,
            AgentVersion: startAgentVersion,
            InputRef: message.InputRef,
            ContextInputs: EmptyInputs,
            CorrelationHeaders: null,
            RetryContext: null,
            ToolExecutionContext: null,
            GlobalContext: message.SharedContext,
            ReviewRound: message.ReviewRound,
            ReviewMaxRounds: message.ReviewMaxRounds));
    }

    /// <summary>
    /// Fires whenever a child saga (one with parent linkage) enters a terminal state. Publishes
    /// <see cref="SubflowCompleted"/> back to the parent saga carrying the child's final
    /// <c>global</c> bag and last-known output ref. The OutputPortName matches the terminal
    /// state name (Completed/Failed/Escalated).
    /// </summary>
    private static async Task PublishSubflowCompletedIfChildAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        string terminalPortName)
    {
        var saga = context.Saga;
        if (saga.ParentTraceId is null
            || saga.ParentNodeId is null
            || saga.ParentRoundId is null)
        {
            return;
        }

        var lastDecision = saga.GetDecisionHistory().LastOrDefault();
        var outputRefStr = lastDecision?.OutputRef ?? saga.CurrentInputRef;
        if (string.IsNullOrWhiteSpace(outputRefStr))
        {
            // Should not happen — child sagas always have at least one decision before terminal,
            // and CurrentInputRef is set at saga init. Bail rather than publish a malformed event.
            return;
        }

        var sharedContext = DeserializeContextInputs(saga.GlobalInputsJson);

        // For ReviewLoop children, the parent drives outcome mapping off Decision (not
        // OutputPortName). Translate the runtime enum to its Contracts counterpart.
        AgentDecisionKind? terminalDecision = lastDecision is null
            ? null
            : MapRuntimeDecisionKindToContract(lastDecision.Decision);

        await context.Publish(new SubflowCompleted(
            ParentTraceId: saga.ParentTraceId.Value,
            ParentNodeId: saga.ParentNodeId.Value,
            ParentRoundId: saga.ParentRoundId.Value,
            ChildTraceId: saga.TraceId,
            OutputPortName: terminalPortName,
            OutputRef: new Uri(outputRefStr),
            SharedContext: sharedContext,
            Decision: terminalDecision,
            ReviewRound: saga.ParentReviewRound,
            TerminalPort: saga.LastEffectivePort));
    }

    private static AgentDecisionKind MapRuntimeDecisionKindToContract(Runtime.AgentDecisionKind kind)
    {
        return kind switch
        {
            Runtime.AgentDecisionKind.Completed => AgentDecisionKind.Completed,
            Runtime.AgentDecisionKind.Approved => AgentDecisionKind.Approved,
            Runtime.AgentDecisionKind.Rejected => AgentDecisionKind.Rejected,
            Runtime.AgentDecisionKind.Failed => AgentDecisionKind.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported runtime decision kind.")
        };
    }

    private readonly record struct ReviewLoopOutcome(
        bool SpawnNextRound,
        int NextRound,
        string? PortName);

    /// <summary>
    /// The default port name that triggers another ReviewLoop iteration when the child's
    /// terminal effective port matches. Kept as a string so workflow authors can override it via
    /// <see cref="WorkflowNode.LoopDecision"/> to use any port name they want (e.g. a socratic
    /// interview loop that uses <c>"Answered"</c> as its loop signal).
    /// </summary>
    public const string DefaultLoopDecision = "Rejected";

    private static string ResolveLoopDecision(WorkflowNode reviewLoopNode)
        => string.IsNullOrWhiteSpace(reviewLoopNode.LoopDecision)
            ? DefaultLoopDecision
            : reviewLoopNode.LoopDecision!.Trim();

    /// <summary>
    /// Maps a child saga's terminal <see cref="SubflowCompleted"/> back onto the ReviewLoop
    /// parent's outcome surface. Priority of signals:
    /// <list type="number">
    /// <item><description><c>TerminalPort</c> (the child saga's last effective port — set by a
    ///   routing script's <c>setNodePath</c> or the decision-kind-derived port name) compared
    ///   case-sensitive against the ReviewLoop node's <c>LoopDecision</c>. Match → iterate (or
    ///   Exhausted on last round).</description></item>
    /// <item><description>Otherwise fall back to <c>Decision</c> — <c>Approved</c>/<c>Completed</c>
    ///   → Approved port; <c>Failed</c> → Failed port; <c>Rejected</c> that didn't match
    ///   <c>LoopDecision</c> → Failed port (a <c>Rejected</c> decision on a ReviewLoop whose
    ///   loop trigger is <em>not</em> "Rejected" is a terminal verdict, not an iterate).</description></item>
    /// <item><description><c>OutputPortName = Escalated</c>/<c>Failed</c> without a matching
    ///   Decision → Failed port.</description></item>
    /// </list>
    /// Using TerminalPort as the primary signal lets workflow authors name any port as the
    /// loop trigger — critical for patterns like a socratic-interview child whose routing
    /// script picks "Rejected" based on a payload field even though the underlying HITL
    /// decision kind was "Completed".
    /// </summary>
    private static ReviewLoopOutcome ResolveReviewLoopOutcome(
        SubflowCompleted message,
        WorkflowNode reviewLoopNode)
    {
        var loopDecision = ResolveLoopDecision(reviewLoopNode);

        // 1. TerminalPort match against the configured LoopDecision.
        if (!string.IsNullOrWhiteSpace(message.TerminalPort)
            && string.Equals(message.TerminalPort, loopDecision, StringComparison.Ordinal))
        {
            var justFinishedRound = message.ReviewRound ?? 1;
            var maxRounds = reviewLoopNode.ReviewMaxRounds ?? 0;
            if (justFinishedRound < maxRounds)
            {
                return new ReviewLoopOutcome(
                    SpawnNextRound: true,
                    NextRound: justFinishedRound + 1,
                    PortName: null);
            }
            return new ReviewLoopOutcome(SpawnNextRound: false, NextRound: 0, PortName: "Exhausted");
        }

        // 2. Fall back to Decision-kind mapping.
        if (message.Decision is AgentDecisionKind decision)
        {
            switch (decision)
            {
                case AgentDecisionKind.Approved:
                case AgentDecisionKind.Completed:
                    return new ReviewLoopOutcome(SpawnNextRound: false, NextRound: 0, PortName: "Approved");

                case AgentDecisionKind.Rejected:
                    // A Rejected Decision that didn't match a "Rejected"-named LoopDecision means
                    // the author reconfigured LoopDecision to something else (e.g. "Answered");
                    // in that case a bare Decision=Rejected is a terminal verdict, not a loop
                    // signal. Route to Failed so the author can handle it explicitly.
                    var justFinishedRound = message.ReviewRound ?? 1;
                    var maxRounds = reviewLoopNode.ReviewMaxRounds ?? 0;
                    if (string.Equals(loopDecision, "Rejected", StringComparison.Ordinal)
                        && justFinishedRound < maxRounds)
                    {
                        return new ReviewLoopOutcome(
                            SpawnNextRound: true,
                            NextRound: justFinishedRound + 1,
                            PortName: null);
                    }
                    if (string.Equals(loopDecision, "Rejected", StringComparison.Ordinal))
                    {
                        return new ReviewLoopOutcome(SpawnNextRound: false, NextRound: 0, PortName: "Exhausted");
                    }
                    return new ReviewLoopOutcome(SpawnNextRound: false, NextRound: 0, PortName: "Failed");

                case AgentDecisionKind.Failed:
                default:
                    return new ReviewLoopOutcome(SpawnNextRound: false, NextRound: 0, PortName: "Failed");
            }
        }

        // 3. No Decision metadata and no matching TerminalPort — treat as Failed.
        return new ReviewLoopOutcome(SpawnNextRound: false, NextRound: 0, PortName: "Failed");
    }

    /// <summary>
    /// Parent-side handler for <see cref="SubflowCompleted"/>. Validates the round, shallow-merges
    /// the child's final <c>global</c> into the parent's <c>global</c> (last-write-wins per
    /// top-level key), records a synthetic decision for the Subflow node, then routes from the
    /// matching output port using the same logic as agent completions.
    /// </summary>
    private static async Task RouteSubflowCompletionAsync(
        BehaviorContext<WorkflowSagaStateEntity, SubflowCompleted> context)
    {
        var saga = context.Saga;
        var message = context.Message;

        using var activity = CodeFlowActivity.StartWorkflowRoot(
            "workflow.saga.route_subflow",
            saga.TraceId);
        activity?.SetTag(CodeFlowActivity.TagNames.RoundId, saga.CurrentRoundId);
        activity?.SetTag(CodeFlowActivity.TagNames.WorkflowKey, saga.WorkflowKey);
        activity?.SetTag(CodeFlowActivity.TagNames.WorkflowVersion, saga.WorkflowVersion);

        // Stale-round rejection — same rationale as RouteCompletionAsync.
        if (message.ParentRoundId != saga.CurrentRoundId)
        {
            activity?.SetTag(CodeFlowActivity.TagNames.SagaState, "StaleRound_Ignored");
            activity?.SetTag("codeflow.saga.message_round_id", message.ParentRoundId);
            return;
        }

        // Shallow merge: last-write-wins per top-level key. Child's working `global` may have
        // accumulated setGlobal writes during its run; flush them into the parent's bag so
        // downstream parent nodes see them.
        if (message.SharedContext.Count > 0)
        {
            var parentGlobal = new Dictionary<string, JsonElement>(
                DeserializeContextInputs(saga.GlobalInputsJson),
                StringComparer.Ordinal);
            foreach (var (key, value) in message.SharedContext)
            {
                parentGlobal[key] = value.Clone();
            }
            saga.GlobalInputsJson = SerializeContextInputs(parentGlobal);
        }

        var services = context.GetPayload<IServiceProvider>();
        var workflowRepo = services.GetRequiredService<IWorkflowRepository>();
        var agentConfigRepo = services.GetRequiredService<IAgentConfigRepository>();
        var artifactStore = services.GetRequiredService<IArtifactStore>();
        var scriptHost = services.GetRequiredService<LogicNodeScriptHost>();

        var workflow = await workflowRepo.GetAsync(
            saga.WorkflowKey,
            saga.WorkflowVersion,
            context.CancellationToken);

        // ReviewLoop parent: map the child's terminal decision to either (a) a specific output
        // port on the ReviewLoop node (Approved/Exhausted/Failed) or (b) a directive to re-invoke
        // the child workflow for the next round. Plain Subflow parents fall through unchanged.
        var parentNode = workflow.FindNode(message.ParentNodeId);
        var effectivePortName = message.OutputPortName;

        if (parentNode?.Kind == WorkflowNodeKind.ReviewLoop)
        {
            var outcome = ResolveReviewLoopOutcome(message, parentNode);

            if (outcome.SpawnNextRound)
            {
                if (saga.SubflowDepth + 1 > MaxSubflowDepth)
                {
                    saga.PendingTransition = PendingTransitionFailed;
                    saga.FailureReason =
                        $"SubflowDepthExceeded: spawning round {outcome.NextRound} of ReviewLoop node "
                        + $"{parentNode.Id} would yield depth {saga.SubflowDepth + 1}, exceeding "
                        + $"the maximum of {MaxSubflowDepth}.";
                    saga.UpdatedAtUtc = DateTime.UtcNow;
                    return;
                }

                await PublishSubflowDispatchAsync(
                    context,
                    saga,
                    workflow,
                    parentNode,
                    inputRef: message.OutputRef,
                    roundId: saga.CurrentRoundId,
                    reviewRound: outcome.NextRound,
                    reviewMaxRounds: parentNode.ReviewMaxRounds,
                    loopDecision: ResolveLoopDecision(parentNode));

                saga.UpdatedAtUtc = DateTime.UtcNow;
                return;
            }

            effectivePortName = outcome.PortName!;
        }

        // Track the parent's effective port so it rides up on TerminalPort if the parent is
        // itself a child of another ReviewLoop (nested case).
        saga.LastEffectivePort = effectivePortName;

        // Synthetic decision record so the parent's history shows the subflow returned a
        // result. Decision kind is Failed for the Failed port, Completed otherwise — best-effort
        // mapping for analytics.
        var syntheticDecisionKind = string.Equals(effectivePortName, "Failed", StringComparison.Ordinal)
            ? Runtime.AgentDecisionKind.Failed
            : Runtime.AgentDecisionKind.Completed;

        saga.AppendDecision(new DecisionRecord(
            AgentKey: string.Empty,
            AgentVersion: 0,
            Decision: syntheticDecisionKind,
            DecisionPayload: null,
            RoundId: saga.CurrentRoundId,
            RecordedAtUtc: DateTime.UtcNow,
            NodeId: message.ParentNodeId,
            OutputPortName: effectivePortName,
            InputRef: saga.CurrentInputRef,
            OutputRef: message.OutputRef.ToString()));

        var edge = workflow.FindNext(message.ParentNodeId, effectivePortName);

        if (edge is null)
        {
            // No edge wired from this port — terminate the parent based on the port name.
            switch (effectivePortName)
            {
                case "Completed":
                case "Approved":
                    saga.PendingTransition = PendingTransitionCompleted;
                    break;
                case "Escalated":
                    saga.PendingTransition = PendingTransitionEscalated;
                    break;
                default:
                    saga.PendingTransition = PendingTransitionFailed;
                    saga.FailureReason ??=
                        $"No outgoing edge from {(parentNode?.Kind == WorkflowNodeKind.ReviewLoop ? "ReviewLoop" : "Subflow")} node {message.ParentNodeId} port '{effectivePortName}'.";
                    break;
            }

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
            saga.FailureReason ??= BuildLogicChainFailureReason(saga);
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

        if ((targetNode.Kind == WorkflowNodeKind.Subflow || targetNode.Kind == WorkflowNodeKind.ReviewLoop)
            && saga.SubflowDepth + 1 > MaxSubflowDepth)
        {
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason =
                $"SubflowDepthExceeded: spawning {targetNode.Kind} node {targetNode.Id} would yield depth "
                + $"{saga.SubflowDepth + 1}, exceeding the maximum of {MaxSubflowDepth}.";
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

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
                    retryContext: null);

                saga.EscalatedFromNodeId = message.ParentNodeId;
                saga.CurrentNodeId = escalationNode.Id;
                saga.CurrentAgentKey = escalationNode.AgentKey ?? string.Empty;
            }
            else
            {
                saga.PendingTransition = PendingTransitionFailed;
                saga.FailureReason =
                    $"Round limit {workflow.MaxRoundsPerRound} exceeded and no escalation node is configured.";
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
            retryContext: null);

        saga.CurrentNodeId = targetNode.Id;
        saga.CurrentAgentKey = targetNode.AgentKey ?? string.Empty;
        saga.CurrentRoundId = targetRoundId;
        saga.RoundCount = targetRoundCount;
        saga.UpdatedAtUtc = DateTime.UtcNow;

        activity?.SetTag(CodeFlowActivity.TagNames.SagaState, saga.PendingTransition ?? "Routed");
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

        // Reject completions from stale or duplicate rounds. Correlation by TraceId alone is
        // not enough — a delayed redelivery or a duplicate publish from an earlier round could
        // otherwise mutate the saga and route to the wrong node.
        if (message.RoundId != saga.CurrentRoundId)
        {
            activity?.SetTag(CodeFlowActivity.TagNames.SagaState, "StaleRound_Ignored");
            activity?.SetTag("codeflow.saga.message_round_id", message.RoundId);
            return;
        }

        var services = context.GetPayload<IServiceProvider>();
        var workflowRepo = services.GetRequiredService<IWorkflowRepository>();
        var agentConfigRepo = services.GetRequiredService<IAgentConfigRepository>();
        var artifactStore = services.GetRequiredService<IArtifactStore>();
        var scriptHost = services.GetRequiredService<LogicNodeScriptHost>();

        var runtimeKind = MapDecisionKind(message.Decision);

        var workflow = await workflowRepo.GetAsync(
            saga.WorkflowKey,
            saga.WorkflowVersion,
            context.CancellationToken);

        if (saga.EscalatedFromNodeId is Guid escalatedFromNodeId)
        {
            saga.AppendDecision(new DecisionRecord(
                AgentKey: message.AgentKey,
                AgentVersion: message.AgentVersion,
                Decision: runtimeKind,
                DecisionPayload: CloneDecisionPayload(message.DecisionPayload),
                RoundId: saga.CurrentRoundId,
                RecordedAtUtc: DateTime.UtcNow,
                NodeId: message.FromNodeId,
                OutputPortName: message.OutputPortName,
                InputRef: saga.CurrentInputRef,
                OutputRef: message.OutputRef?.ToString()));

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

        var portResolution = await ResolveSourcePortAsync(
            context,
            workflow,
            saga,
            scriptHost,
            artifactStore,
            message);

        var effectivePortName = portResolution.Port;
        var effectiveOutputRef = portResolution.OverrideOutputRef ?? message.OutputRef;

        // Append decision *after* the script runs so the OutputRef reflects any setOutput()
        // override. The original output artifact is preserved in the store; only the pointer
        // recorded on the decision and used for downstream dispatch is swapped.
        saga.AppendDecision(new DecisionRecord(
            AgentKey: message.AgentKey,
            AgentVersion: message.AgentVersion,
            Decision: runtimeKind,
            DecisionPayload: CloneDecisionPayload(message.DecisionPayload),
            RoundId: saga.CurrentRoundId,
            RecordedAtUtc: DateTime.UtcNow,
            NodeId: message.FromNodeId,
            OutputPortName: message.OutputPortName,
            InputRef: saga.CurrentInputRef,
            OutputRef: effectiveOutputRef?.ToString()));

        // Remember the effective port so the terminal SubflowCompleted can carry it as
        // TerminalPort — the ReviewLoop parent compares that against its configured
        // LoopDecision to decide whether to iterate or exit.
        saga.LastEffectivePort = effectivePortName;

        var edge = workflow.FindNext(message.FromNodeId, effectivePortName);

        if (edge is null)
        {
            // Unwired-port exit rules:
            //   - Completed: always a legal clean exit (top-level and subflow).
            //   - Approved / Rejected: a legal clean exit *for child sagas* — the decision kind
            //     is preserved on SubflowCompleted.Decision so parents (especially ReviewLoop)
            //     can route on the last agent's intent.
            //   - Match for the parent's ReviewLoop LoopDecision (propagated to the child saga
            //     at dispatch): also a legal clean exit, so authors can use any port name as
            //     the loop trigger without the child saga failing on the unwired port.
            //   - Anything else (Failed, Escalated, top-level with non-Completed, typos): the
            //     port is unexpectedly unwired; fail with a clear reason.
            var isChildSaga = saga.ParentTraceId is not null;
            var matchesParentLoopDecision = isChildSaga
                && !string.IsNullOrWhiteSpace(saga.ParentLoopDecision)
                && string.Equals(effectivePortName, saga.ParentLoopDecision, StringComparison.Ordinal);

            if (message.Decision == AgentDecisionKind.Completed
                || (isChildSaga && (message.Decision == AgentDecisionKind.Approved
                                    || message.Decision == AgentDecisionKind.Rejected))
                || matchesParentLoopDecision)
            {
                saga.PendingTransition = PendingTransitionCompleted;
            }
            else
            {
                saga.PendingTransition = PendingTransitionFailed;
                saga.FailureReason = $"No outgoing edge from node {message.FromNodeId} port '{effectivePortName}'.";
            }

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
            upstreamOutputRef: effectiveOutputRef);

        if (resolution is { FailureTerminal: true })
        {
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason ??= BuildLogicChainFailureReason(saga);
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
                    inputRef: effectiveOutputRef,
                    roundId: saga.CurrentRoundId,
                    retryContext: retryContext);

                saga.EscalatedFromNodeId = message.FromNodeId;
                saga.CurrentNodeId = escalationNode.Id;
                saga.CurrentAgentKey = escalationNode.AgentKey ?? string.Empty;
            }
            else
            {
                saga.PendingTransition = PendingTransitionFailed;
                saga.FailureReason = $"Round limit {workflow.MaxRoundsPerRound} exceeded and no escalation node is configured.";
            }

            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        if ((targetNode.Kind == WorkflowNodeKind.Subflow || targetNode.Kind == WorkflowNodeKind.ReviewLoop)
            && saga.SubflowDepth + 1 > MaxSubflowDepth)
        {
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason =
                $"SubflowDepthExceeded: spawning {targetNode.Kind} node {targetNode.Id} would yield depth "
                + $"{saga.SubflowDepth + 1}, exceeding the maximum of {MaxSubflowDepth}.";
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        await DispatchToNodeAsync(
            context,
            agentConfigRepo,
            saga,
            workflow,
            targetNode,
            inputRef: effectiveOutputRef,
            roundId: targetRoundId,
            retryContext: retryContext);

        saga.CurrentNodeId = targetNode.Id;
        saga.CurrentAgentKey = targetNode.AgentKey ?? string.Empty;
        saga.CurrentRoundId = targetRoundId;
        saga.RoundCount = targetRoundCount;
        saga.UpdatedAtUtc = DateTime.UtcNow;

        activity?.SetTag(CodeFlowActivity.TagNames.SagaState, saga.PendingTransition ?? "Routed");
    }

    private readonly record struct SourcePortResolution(string Port, Uri? OverrideOutputRef);

    /// <summary>
    /// If the source node (the one that just emitted <see cref="AgentInvocationCompleted"/>) is an
    /// Agent/HITL/Escalation/Start node with a non-empty script, evaluate the script to pick the
    /// outgoing port. The script sees the agent's output artifact as <c>input</c> (with
    /// <c>input.decision</c>, <c>input.decisionKind</c>, <c>input.outputPortName</c>, and
    /// <c>input.decisionPayload</c> attached) and the workflow
    /// <c>context</c>. On any failure or if <c>setNodePath</c> is not called, fall back to the
    /// AgentDecisionKind-named port carried on the completion message. Context writes made via
    /// <c>setContext</c> are merged into <see cref="WorkflowSagaStateEntity.InputsJson"/>.
    ///
    /// If the script calls <c>setOutput(text)</c>, the provided string is persisted as a new
    /// artifact and returned via <see cref="SourcePortResolution.OverrideOutputRef"/>. The
    /// original artifact is left in the store untouched — the override only affects the URI
    /// used for the saga's DecisionRecord and for downstream dispatch.
    /// </summary>
    private static async Task<SourcePortResolution> ResolveSourcePortAsync(
        BehaviorContext<WorkflowSagaStateEntity, AgentInvocationCompleted> context,
        Workflow workflow,
        WorkflowSagaStateEntity saga,
        LogicNodeScriptHost scriptHost,
        IArtifactStore artifactStore,
        AgentInvocationCompleted message)
    {
        var fallbackPort = message.OutputPortName;
        var fromNode = workflow.FindNode(message.FromNodeId);
        if (fromNode is null
            || fromNode.Kind == WorkflowNodeKind.Logic
            || string.IsNullOrWhiteSpace(fromNode.Script))
        {
            return new SourcePortResolution(fallbackPort, null);
        }

        if (message.OutputRef is null)
        {
            return new SourcePortResolution(fallbackPort, null);
        }

        var contextInputs = DeserializeContextInputs(saga.InputsJson);
        var globalInputs = DeserializeContextInputs(saga.GlobalInputsJson);
        var artifactJson = await ReadArtifactAsJsonAsync(
            artifactStore,
            message.OutputRef,
            context.CancellationToken);
        var scriptInput = ComposeAgentScriptInput(artifactJson, message.Decision, message.DecisionPayload);

        var eval = scriptHost.Evaluate(
            workflowKey: workflow.Key,
            workflowVersion: workflow.Version,
            nodeId: fromNode.Id,
            script: fromNode.Script!,
            declaredPorts: fromNode.OutputPorts,
            input: scriptInput,
            context: contextInputs,
            cancellationToken: context.CancellationToken,
            global: globalInputs,
            reviewRound: saga.ParentReviewRound,
            reviewMaxRounds: saga.ParentReviewMaxRounds,
            allowOutputOverride: true);

        saga.AppendLogicEvaluation(new LogicEvaluationRecord(
            NodeId: fromNode.Id,
            OutputPortName: eval.OutputPortName,
            RoundId: saga.CurrentRoundId,
            Duration: eval.Duration,
            Logs: eval.LogEntries,
            FailureKind: eval.Failure?.ToString(),
            FailureMessage: eval.FailureMessage,
            RecordedAtUtc: DateTime.UtcNow));

        ApplyScriptUpdates(saga, contextInputs, globalInputs, eval);

        if (!eval.IsSuccess)
        {
            return new SourcePortResolution(fallbackPort, null);
        }

        Uri? overrideRef = null;
        if (!string.IsNullOrEmpty(eval.OutputOverride))
        {
            overrideRef = await WriteOverrideArtifactAsync(
                artifactStore,
                saga,
                fromNode.AgentKey,
                eval.OutputOverride!,
                context.CancellationToken);
        }

        return new SourcePortResolution(eval.OutputPortName!, overrideRef);
    }

    private static async Task<Uri> WriteOverrideArtifactAsync(
        IArtifactStore artifactStore,
        WorkflowSagaStateEntity saga,
        string? agentKey,
        string content,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        var fileNamePrefix = string.IsNullOrWhiteSpace(agentKey) ? "node" : agentKey;
        var metadata = new ArtifactMetadata(
            TraceId: saga.TraceId,
            RoundId: saga.CurrentRoundId,
            ArtifactId: Guid.NewGuid(),
            ContentType: "text/plain",
            FileName: $"{fileNamePrefix}-scripted-output.txt");
        return await artifactStore.WriteAsync(stream, metadata, cancellationToken);
    }

    private static void ApplyScriptUpdates(
        WorkflowSagaStateEntity saga,
        IReadOnlyDictionary<string, JsonElement> currentLocal,
        IReadOnlyDictionary<string, JsonElement> currentGlobal,
        LogicNodeEvaluationResult eval)
    {
        if (eval.ContextUpdates.Count > 0)
        {
            var mergedLocal = new Dictionary<string, JsonElement>(currentLocal, StringComparer.Ordinal);
            foreach (var (key, value) in eval.ContextUpdates)
            {
                mergedLocal[key] = value;
            }
            saga.InputsJson = SerializeContextInputs(mergedLocal);
        }

        if (eval.GlobalUpdates.Count > 0)
        {
            var mergedGlobal = new Dictionary<string, JsonElement>(currentGlobal, StringComparer.Ordinal);
            foreach (var (key, value) in eval.GlobalUpdates)
            {
                mergedGlobal[key] = value;
            }
            saga.GlobalInputsJson = SerializeContextInputs(mergedGlobal);
        }
    }

    private static JsonElement ComposeAgentScriptInput(
        JsonElement artifactJson,
        AgentDecisionKind decision,
        JsonElement? decisionPayload)
    {
        var decisionKindText = decision.ToString();
        var outputPortName = TryReadOutputPortName(decisionPayload) ?? decisionKindText;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            if (artifactJson.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in artifactJson.EnumerateObject())
                {
                    // Never let the artifact shadow the decision metadata we inject below.
                    if (string.Equals(property.Name, "decision", StringComparison.Ordinal)
                        || string.Equals(property.Name, "decisionKind", StringComparison.Ordinal)
                        || string.Equals(property.Name, "outputPortName", StringComparison.Ordinal)
                        || string.Equals(property.Name, "decisionPayload", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    property.WriteTo(writer);
                }
            }
            else
            {
                writer.WritePropertyName("value");
                artifactJson.WriteTo(writer);
            }

            writer.WriteString("decision", outputPortName);
            writer.WriteString("decisionKind", decisionKindText);
            writer.WriteString("outputPortName", outputPortName);

            writer.WritePropertyName("decisionPayload");
            if (decisionPayload is { } payload)
            {
                payload.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    private static string? TryReadOutputPortName(JsonElement? decisionPayload)
    {
        if (decisionPayload is not { ValueKind: JsonValueKind.Object } payload)
        {
            return null;
        }

        if (!payload.TryGetProperty("outputPortName", out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static async Task<LogicChainResolution?> ResolveTargetThroughLogicChainAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
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
        var globalInputs = DeserializeContextInputs(saga.GlobalInputsJson);

        for (var hops = 0; hops < MaxLogicChainHops; hops++)
        {
            if (!inputLoaded)
            {
                inputJson = await ReadArtifactAsJsonAsync(artifactStore, upstreamOutputRef, context.CancellationToken);
                inputLoaded = true;
            }

            if (string.IsNullOrWhiteSpace(currentNode.Script))
            {
                var reason = $"Logic node {currentNode.Id} has no script.";
                saga.AppendLogicEvaluation(LogicEvaluationRecordFailure(
                    currentNode.Id,
                    saga.CurrentRoundId,
                    TimeSpan.Zero,
                    Array.Empty<string>(),
                    kind: "ConfigurationError",
                    message: reason));
                saga.FailureReason = reason;
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
                cancellationToken: context.CancellationToken,
                global: globalInputs,
                reviewRound: saga.ParentReviewRound,
                reviewMaxRounds: saga.ParentReviewMaxRounds);

            saga.AppendLogicEvaluation(new LogicEvaluationRecord(
                NodeId: currentNode.Id,
                OutputPortName: eval.OutputPortName,
                RoundId: saga.CurrentRoundId,
                Duration: eval.Duration,
                Logs: eval.LogEntries,
                FailureKind: eval.Failure?.ToString(),
                FailureMessage: eval.FailureMessage,
                RecordedAtUtc: DateTime.UtcNow));

            ApplyScriptUpdates(saga, contextInputs, globalInputs, eval);
            if (eval.ContextUpdates.Count > 0)
            {
                var merged = new Dictionary<string, JsonElement>(contextInputs, StringComparer.Ordinal);
                foreach (var (key, value) in eval.ContextUpdates)
                {
                    merged[key] = value;
                }
                contextInputs = merged;
            }
            if (eval.GlobalUpdates.Count > 0)
            {
                var merged = new Dictionary<string, JsonElement>(globalInputs, StringComparer.Ordinal);
                foreach (var (key, value) in eval.GlobalUpdates)
                {
                    merged[key] = value;
                }
                globalInputs = merged;
            }

            var chosenPort = eval.IsSuccess
                ? eval.OutputPortName!
                : AgentDecisionPorts.FailedPort;

            var nextEdge = workflow.FindNext(currentNode.Id, chosenPort);
            if (nextEdge is null)
            {
                saga.FailureReason = eval.IsSuccess
                    ? $"Logic node {currentNode.Id} emitted port '{chosenPort}' but no outgoing edge is connected."
                    : $"Logic node {currentNode.Id} failed ({eval.Failure}) and has no '{AgentDecisionPorts.FailedPort}' edge.";
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

        var tooLongReason = $"Logic chain exceeded {MaxLogicChainHops} hops.";
        saga.AppendLogicEvaluation(LogicEvaluationRecordFailure(
            currentNode.Id,
            saga.CurrentRoundId,
            TimeSpan.Zero,
            Array.Empty<string>(),
            kind: "LogicChainTooLong",
            message: tooLongReason));
        saga.FailureReason = tooLongReason;
        return new LogicChainResolution(null, rotates, FailureTerminal: true);
    }

    private static string BuildLogicChainFailureReason(WorkflowSagaStateEntity saga)
    {
        var last = saga.GetLogicEvaluationHistory().LastOrDefault();
        if (last is null)
        {
            return "Logic chain terminated without a downstream edge.";
        }

        if (!string.IsNullOrWhiteSpace(last.FailureMessage))
        {
            return last.FailureMessage!;
        }

        return $"Logic node {last.NodeId} emitted port '{last.OutputPortName}' but no outgoing edge is connected.";
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
        BehaviorContext<WorkflowSagaStateEntity> context,
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
            WorkflowNodeKind.Subflow =>
                PublishSubflowDispatchAsync(context, saga, workflow, node, inputRef, roundId),
            WorkflowNodeKind.ReviewLoop =>
                PublishSubflowDispatchAsync(
                    context,
                    saga,
                    workflow,
                    node,
                    inputRef,
                    roundId,
                    reviewRound: 1,
                    reviewMaxRounds: node.ReviewMaxRounds
                        ?? throw new InvalidOperationException(
                            $"ReviewLoop node {node.Id} in workflow {workflow.Key} v{workflow.Version} has no ReviewMaxRounds configured."),
                    loopDecision: ResolveLoopDecision(node)),
            WorkflowNodeKind.Logic =>
                throw new InvalidOperationException(
                    "Logic nodes should have been resolved by the logic chain resolver before reaching DispatchToNodeAsync."),
            _ =>
                throw new InvalidOperationException($"Unknown workflow node kind: {node.Kind}.")
        };
    }

    private static Task PublishSubflowDispatchAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode subflowNode,
        Uri inputRef,
        Guid roundId,
        int? reviewRound = null,
        int? reviewMaxRounds = null,
        string? loopDecision = null)
    {
        if (string.IsNullOrWhiteSpace(subflowNode.SubflowKey))
        {
            throw new InvalidOperationException(
                $"Subflow node {subflowNode.Id} in workflow {workflow.Key} v{workflow.Version} "
                + "has no SubflowKey configured.");
        }

        if (subflowNode.SubflowVersion is not int subflowVersion)
        {
            throw new InvalidOperationException(
                $"Subflow node {subflowNode.Id} in workflow {workflow.Key} v{workflow.Version} "
                + "has no pinned SubflowVersion. Latest-version resolution must happen at "
                + "parent-workflow save time, not at saga dispatch.");
        }

        saga.CurrentInputRef = inputRef.ToString();

        var sharedContext = DeserializeContextInputs(saga.GlobalInputsJson);

        return context.Publish(new SubflowInvokeRequested(
            ParentTraceId: saga.TraceId,
            ParentNodeId: subflowNode.Id,
            ParentRoundId: roundId,
            ChildTraceId: Guid.NewGuid(),
            SubflowKey: subflowNode.SubflowKey,
            SubflowVersion: subflowVersion,
            InputRef: inputRef,
            SharedContext: sharedContext,
            Depth: saga.SubflowDepth + 1,
            ReviewRound: reviewRound,
            ReviewMaxRounds: reviewMaxRounds,
            LoopDecision: loopDecision));
    }

    private static async Task PublishHandoffAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
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

        saga.CurrentInputRef = inputRef.ToString();

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
            RetryContext: retryContext,
            GlobalContext: DeserializeContextInputs(saga.GlobalInputsJson),
            ReviewRound: saga.ParentReviewRound,
            ReviewMaxRounds: saga.ParentReviewMaxRounds));
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
