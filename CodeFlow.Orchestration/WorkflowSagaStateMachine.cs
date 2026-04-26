using CodeFlow.Contracts;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Observability;
using CodeFlow.Runtime.Workspace;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace CodeFlow.Orchestration;

public sealed class WorkflowSagaStateMachine : MassTransitStateMachine<WorkflowSagaStateEntity>
{
    public const string PendingTransitionCompleted = "Completed";
    public const string PendingTransitionFailed = "Failed";

    /// <summary>
    /// Implicit error sink port name. Every node implicitly carries this port; if wired, the
    /// saga routes runtime errors and explicit <c>fail</c> tool calls down it. If unwired, the
    /// saga terminates with <c>FailureReason</c> set.
    /// </summary>
    public const string ImplicitFailedPort = "Failed";

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

        During(Running,
            When(AgentInvocationCompletedEvent)
                .ThenAsync(context => RouteCompletionAsync(context))
                .IfElse(
                    context => context.Saga.PendingTransition == PendingTransitionCompleted,
                    completeBinder => completeBinder
                        .Then(ClearPendingTransition)
                        .TransitionTo(Completed),
                    continueBinder => continueBinder
                        .If(
                            context => context.Saga.PendingTransition == PendingTransitionFailed,
                            failBinder => failBinder
                                .Then(ClearPendingTransition)
                                .TransitionTo(Failed))),
            When(SubflowCompletedEvent)
                .ThenAsync(context => RouteSubflowCompletionAsync(context))
                .IfElse(
                    context => context.Saga.PendingTransition == PendingTransitionCompleted,
                    completeBinder => completeBinder
                        .Then(ClearPendingTransition)
                        .TransitionTo(Completed),
                    continueBinder => continueBinder
                        .If(
                            context => context.Saga.PendingTransition == PendingTransitionFailed,
                            failBinder => failBinder
                                .Then(ClearPendingTransition)
                                .TransitionTo(Failed))));

        WhenEnter(Completed, binder => binder.ThenAsync(context => PublishSubflowCompletedIfChildAsync(context, "Completed")));
        WhenEnter(Completed, binder => binder.ThenAsync(TryCleanupHappyPathWorkdirAsync));
        WhenEnter(Failed, binder => binder.ThenAsync(context => PublishSubflowCompletedIfChildAsync(context, "Failed")));
    }

    /// <summary>
    /// Happy-path workdir cleanup. Fires when a top-level saga reaches <see cref="Completed"/>
    /// AND every entry in <c>context.repositories</c> has a non-empty <c>prUrl</c> (set by the
    /// publish agent via <c>setContext</c>). If either condition fails — child saga, no
    /// repositories array, or any repo missing a PR URL — the workdir is left in place so an
    /// operator can inspect what went wrong. Slice F's periodic sweep catches anything that's
    /// genuinely orphaned past the configured TTL.
    /// </summary>
    private static async Task TryCleanupHappyPathWorkdirAsync(
        BehaviorContext<WorkflowSagaStateEntity> context)
    {
        var saga = context.Saga;

        // Subflow children share the parent's workdir — cleanup happens once at the top level.
        if (saga.ParentTraceId is not null)
        {
            return;
        }

        if (!AllRepositoriesHavePrUrl(saga.InputsJson))
        {
            return;
        }

        var services = context.GetPayload<IServiceProvider>();
        var settingsRepo = services.GetRequiredService<IGitHostSettingsRepository>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        Runtime.Workspace.GitHostSettings? settings;
        try
        {
            settings = await settingsRepo.GetAsync();
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger<WorkflowSagaStateMachine>().LogWarning(
                ex,
                "Could not read GitHostSettings for happy-path workdir cleanup of trace {TraceId}; skipping.",
                saga.TraceId);
            return;
        }

        TraceWorkdirCleanup.TryRemove(
            settings?.WorkingDirectoryRoot,
            saga.TraceId,
            loggerFactory.CreateLogger<WorkflowSagaStateMachine>());
    }

    /// <summary>
    /// Returns true iff <paramref name="inputsJson"/> contains a non-empty <c>repositories</c>
    /// array AND every entry has a non-empty <c>prUrl</c> string. Any other shape returns false
    /// (i.e. workflow had no repos, or some repo failed to publish, or the field is missing).
    /// Public for unit-testability — small pure predicate, no security implications.
    /// </summary>
    public static bool AllRepositoriesHavePrUrl(string? inputsJson)
    {
        if (string.IsNullOrWhiteSpace(inputsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(inputsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("repositories", out var repos)
                || repos.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var seen = false;
            foreach (var entry in repos.EnumerateArray())
            {
                seen = true;
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("prUrl", out var prUrl)
                    || prUrl.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(prUrl.GetString()))
                {
                    return false;
                }
            }

            return seen;
        }
        catch (JsonException)
        {
            return false;
        }
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
        if (message.WorkflowContext is not null)
        {
            saga.WorkflowInputsJson = SerializeContextInputs(message.WorkflowContext);
        }
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
    /// workflow, copies parent linkage + workflow snapshot onto the saga, pins the child
    /// Start's agent version, and publishes an <see cref="AgentInvokeRequested"/> for the child
    /// Start node so the existing agent invocation pipeline picks it up. The published
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
        var scriptHost = services.GetRequiredService<LogicNodeScriptHost>();
        var artifactStore = services.GetRequiredService<IArtifactStore>();

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
        saga.WorkflowInputsJson = SerializeContextInputs(message.WorkflowContext);
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

        var inputOutcome = await TryEvaluateInputScriptAsync(
            context, saga, workflow, startNode, message.InputRef, scriptHost, artifactStore);
        if (inputOutcome.Failed)
        {
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason = inputOutcome.FailureReason;
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        var effectiveInputRef = inputOutcome.InputRef!;
        saga.CurrentInputRef = effectiveInputRef.ToString();

        // The child's local `context` starts empty — the parent's shared snapshot belongs on
        // `workflow`, not on the child's local inputs. Publishing WorkflowContext as
        // ContextInputs would make parent keys show up under {{context.*}} on the child Start,
        // and {{workflow.*}} would be empty — the opposite of the documented semantics.
        await context.Publish(new AgentInvokeRequested(
            TraceId: message.ChildTraceId,
            RoundId: childRoundId,
            WorkflowKey: workflow.Key,
            WorkflowVersion: workflow.Version,
            NodeId: startNode.Id,
            AgentKey: startNode.AgentKey,
            AgentVersion: startAgentVersion,
            InputRef: effectiveInputRef,
            ContextInputs: EmptyInputs,
            CorrelationHeaders: null,
            RetryContext: null,
            ToolExecutionContext: null,
            WorkflowContext: DeserializeContextInputs(saga.WorkflowInputsJson),
            ReviewRound: message.ReviewRound,
            ReviewMaxRounds: message.ReviewMaxRounds));
    }

    /// <summary>
    /// Fires whenever a child saga (one with parent linkage) enters a terminal state. Publishes
    /// <see cref="SubflowCompleted"/> back to the parent saga carrying the child's final
    /// <c>workflow</c> bag and last-known output ref. The OutputPortName matches the terminal
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

        var workflowContext = DeserializeContextInputs(saga.WorkflowInputsJson);

        // For ReviewLoop children, the parent drives outcome mapping off Decision (not
        // OutputPortName). Both are now plain port-name strings.
        var terminalDecision = lastDecision?.Decision;

        await context.Publish(new SubflowCompleted(
            ParentTraceId: saga.ParentTraceId.Value,
            ParentNodeId: saga.ParentNodeId.Value,
            ParentRoundId: saga.ParentRoundId.Value,
            ChildTraceId: saga.TraceId,
            OutputPortName: terminalPortName,
            OutputRef: new Uri(outputRefStr),
            WorkflowContext: workflowContext,
            Decision: terminalDecision,
            ReviewRound: saga.ParentReviewRound,
            TerminalPort: saga.LastEffectivePort));
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
    /// Builds the AgentKey recorded on the synthetic decision a parent saga writes when a
    /// Subflow/ReviewLoop child terminates. Subflow nodes have no agent, so we synthesize a
    /// descriptive label from the node kind plus the subflow it invokes — otherwise the trace UI
    /// shows the timeline entry with a blank title. Falls back gracefully when the parent node is
    /// unexpectedly null or lacks a subflow key.
    /// </summary>
    private static string BuildSubflowSyntheticAgentKey(WorkflowNode? parentNode)
    {
        if (parentNode is null)
        {
            return "subflow";
        }

        var prefix = parentNode.Kind == WorkflowNodeKind.ReviewLoop ? "review-loop" : "subflow";
        var key = parentNode.SubflowKey?.Trim();
        return string.IsNullOrEmpty(key) ? prefix : $"{prefix}:{key}";
    }

    /// <summary>
    /// Maps a child saga's terminal <see cref="SubflowCompleted"/> back onto the ReviewLoop
    /// parent's outcome port. The new model:
    /// <list type="number">
    /// <item><description>If the child's terminal port (<c>TerminalPort</c>, falling back to
    ///   <c>OutputPortName</c>) matches the ReviewLoop node's configured <c>LoopDecision</c>,
    ///   spawn the next round (or synthesize <c>Exhausted</c> when the round budget is
    ///   spent).</description></item>
    /// <item><description>Otherwise, propagate the child's terminal port name verbatim. The
    ///   parent saga then looks for an outgoing edge from the ReviewLoop node on that exact
    ///   port — author-defined names flow straight through, no enum mapping.</description></item>
    /// <item><description>The implicit <c>Failed</c> port is treated like any other port name —
    ///   it propagates verbatim and the parent saga's unwired-port logic handles the
    ///   "no Failed edge → terminate Failed" decision.</description></item>
    /// </list>
    /// </summary>
    private static ReviewLoopOutcome ResolveReviewLoopOutcome(
        SubflowCompleted message,
        WorkflowNode reviewLoopNode)
    {
        var loopDecision = ResolveLoopDecision(reviewLoopNode);
        var terminalPort = !string.IsNullOrWhiteSpace(message.TerminalPort)
            ? message.TerminalPort!
            : message.OutputPortName;

        if (!string.IsNullOrWhiteSpace(terminalPort)
            && string.Equals(terminalPort, loopDecision, StringComparison.Ordinal))
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

        var exitPort = !string.IsNullOrWhiteSpace(terminalPort)
            ? terminalPort!
            : ImplicitFailedPort;
        return new ReviewLoopOutcome(SpawnNextRound: false, NextRound: 0, PortName: exitPort);
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

        // Shallow merge: last-write-wins per top-level key. Child's working `workflow` may have
        // accumulated setWorkflow writes during its run; flush them into the parent's bag so
        // downstream parent nodes see them.
        if (message.WorkflowContext.Count > 0)
        {
            var parentWorkflow = new Dictionary<string, JsonElement>(
                DeserializeContextInputs(saga.WorkflowInputsJson),
                StringComparer.Ordinal);
            foreach (var (key, value) in message.WorkflowContext)
            {
                parentWorkflow[key] = value.Clone();
            }
            saga.WorkflowInputsJson = SerializeContextInputs(parentWorkflow);
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
        // result. The Decision is the effective port name verbatim — author-defined names
        // propagate through the trace UI.

        // AgentKey is what the trace UI surfaces as the timeline entry's title. Subflow/ReviewLoop
        // nodes have no agent of their own, so synthesize a descriptive label from the node kind
        // plus the subflow key the node invokes — otherwise the parent trace shows a title-less row.
        saga.AppendDecision(new DecisionRecord(
            AgentKey: BuildSubflowSyntheticAgentKey(parentNode),
            AgentVersion: 0,
            Decision: effectivePortName,
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
            // Unwired-port exit on the parent's Subflow/ReviewLoop node:
            //   - The implicit Failed port: parent terminates Failed with FailureReason set.
            //   - Any other port name: parent terminates cleanly with the port name preserved on
            //     saga.LastEffectivePort. (If this saga itself is a child, that name rides up via
            //     SubflowCompleted to its own parent.)
            if (string.Equals(effectivePortName, ImplicitFailedPort, StringComparison.Ordinal))
            {
                saga.PendingTransition = PendingTransitionFailed;
                saga.FailureReason ??=
                    $"No outgoing edge from {(parentNode?.Kind == WorkflowNodeKind.ReviewLoop ? "ReviewLoop" : "Subflow")} node {message.ParentNodeId} port '{effectivePortName}'.";
            }
            else
            {
                saga.PendingTransition = PendingTransitionCompleted;
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
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason ??= $"Round limit {workflow.MaxRoundsPerRound} exceeded.";
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        await DispatchToNodeAsync(
            context,
            agentConfigRepo,
            scriptHost,
            artifactStore,
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
        activity?.SetTag(CodeFlowActivity.TagNames.DecisionKind, message.OutputPortName);

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

        var decisionPortName = message.OutputPortName;

        var workflow = await workflowRepo.GetAsync(
            saga.WorkflowKey,
            saga.WorkflowVersion,
            context.CancellationToken);

        var portResolution = await ResolveSourcePortAsync(
            context,
            workflow,
            saga,
            scriptHost,
            artifactStore,
            message);

        var effectivePortName = portResolution.Port;
        var effectiveOutputRef = portResolution.OverrideOutputRef ?? message.OutputRef;

        // Decision output template: if no setOutput() override was applied and the agent declares
        // a matching per-decision template, render it server-side and substitute the effective
        // output ref. Script overrides always win so authors have an explicit escape hatch.
        string? decisionTemplateFailure = null;
        if (portResolution.OverrideOutputRef is null
            && effectiveOutputRef is not null
            && !string.IsNullOrWhiteSpace(message.AgentKey))
        {
            var templateRenderer = services.GetRequiredService<Runtime.IScribanTemplateRenderer>();
            var templateOutcome = await TryApplyDecisionOutputTemplateAsync(
                context,
                agentConfigRepo,
                artifactStore,
                templateRenderer,
                saga,
                message,
                effectivePortName,
                effectiveOutputRef);

            if (templateOutcome.FailureReason is not null)
            {
                decisionTemplateFailure = templateOutcome.FailureReason;
            }
            else if (templateOutcome.OverrideOutputRef is not null)
            {
                effectiveOutputRef = templateOutcome.OverrideOutputRef;
            }
        }

        // Append decision *after* the script runs so the OutputRef reflects any setOutput()
        // override or decision-output template. The original artifact is preserved in the store;
        // only the pointer recorded on the decision and used for downstream dispatch is swapped.
        saga.AppendDecision(new DecisionRecord(
            AgentKey: message.AgentKey,
            AgentVersion: message.AgentVersion,
            Decision: decisionPortName,
            DecisionPayload: CloneDecisionPayload(message.DecisionPayload),
            RoundId: saga.CurrentRoundId,
            RecordedAtUtc: DateTime.UtcNow,
            NodeId: message.FromNodeId,
            OutputPortName: message.OutputPortName,
            InputRef: saga.CurrentInputRef,
            OutputRef: effectiveOutputRef?.ToString()));

        // Slice 13: apply any setContext / setWorkflow writes the agent issued during its turn.
        // These are committed only when the runtime publishes a non-Failed decision (the loop
        // already discards them on failure); here we just merge them into the saga's bags so the
        // next downstream agent sees the new values.
        ApplyAgentBagWrites(saga, message);

        if (decisionTemplateFailure is not null)
        {
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason = decisionTemplateFailure;
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        // Remember the effective port so the terminal SubflowCompleted can carry it as
        // TerminalPort — the ReviewLoop parent compares that against its configured
        // LoopDecision to decide whether to iterate or exit.
        saga.LastEffectivePort = effectivePortName;

        var edge = workflow.FindNext(message.FromNodeId, effectivePortName);

        if (edge is null)
        {
            // Unwired-port exit rules in the new model:
            //   - The implicit Failed port: terminate the saga as Failed with FailureReason set.
            //     Authors can wire a Failed edge to override; if they don't, the saga fails so
            //     the runtime error doesn't get silently swallowed.
            //   - Any other port name (author-defined): terminate cleanly. The terminal port name
            //     is preserved on saga.LastEffectivePort and rides up to the parent saga as
            //     SubflowCompleted.OutputPortName / TerminalPort, so a parent Subflow node can
            //     route from that exact port. Top-level workflows just complete with the terminal
            //     name surfaced on the trace UI.
            if (string.Equals(effectivePortName, ImplicitFailedPort, StringComparison.Ordinal))
            {
                saga.PendingTransition = PendingTransitionFailed;
                saga.FailureReason ??=
                    $"No outgoing edge from node {message.FromNodeId} port '{effectivePortName}'.";
            }
            else
            {
                saga.PendingTransition = PendingTransitionCompleted;
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
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason = $"Round limit {workflow.MaxRoundsPerRound} exceeded.";
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
            scriptHost,
            artifactStore,
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
            || string.IsNullOrWhiteSpace(fromNode.OutputScript))
        {
            return new SourcePortResolution(fallbackPort, null);
        }

        if (message.OutputRef is null)
        {
            return new SourcePortResolution(fallbackPort, null);
        }

        var contextInputs = DeserializeContextInputs(saga.InputsJson);
        var workflowInputs = DeserializeContextInputs(saga.WorkflowInputsJson);
        var artifactJson = await ReadArtifactAsJsonAsync(
            artifactStore,
            message.OutputRef,
            context.CancellationToken);
        var scriptInput = ComposeAgentScriptInput(artifactJson, message.OutputPortName, message.DecisionPayload);

        var eval = scriptHost.Evaluate(
            workflowKey: workflow.Key,
            workflowVersion: workflow.Version,
            nodeId: fromNode.Id,
            script: fromNode.OutputScript!,
            declaredPorts: fromNode.OutputPorts,
            input: scriptInput,
            context: contextInputs,
            cancellationToken: context.CancellationToken,
            workflow: workflowInputs,
            reviewRound: saga.ParentReviewRound,
            reviewMaxRounds: saga.ParentReviewMaxRounds,
            allowOutputOverride: true,
            inputVariableName: "output");

        saga.AppendLogicEvaluation(new LogicEvaluationRecord(
            NodeId: fromNode.Id,
            OutputPortName: eval.OutputPortName,
            RoundId: saga.CurrentRoundId,
            Duration: eval.Duration,
            Logs: eval.LogEntries,
            FailureKind: eval.Failure?.ToString(),
            FailureMessage: eval.FailureMessage,
            RecordedAtUtc: DateTime.UtcNow));

        ApplyScriptUpdates(saga, contextInputs, workflowInputs, eval);

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
        CancellationToken cancellationToken,
        string fileNameSuffix = "scripted-output")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        var fileNamePrefix = string.IsNullOrWhiteSpace(agentKey) ? "node" : agentKey;
        var metadata = new ArtifactMetadata(
            TraceId: saga.TraceId,
            RoundId: saga.CurrentRoundId,
            ArtifactId: Guid.NewGuid(),
            ContentType: "text/plain",
            FileName: $"{fileNamePrefix}-{fileNameSuffix}.txt");
        return await artifactStore.WriteAsync(stream, metadata, cancellationToken);
    }

    private readonly record struct InputScriptOutcome(Uri? InputRef, string? FailureReason)
    {
        public bool Failed => FailureReason is not null;
        public static InputScriptOutcome Ok(Uri inputRef) => new(inputRef, null);
        public static InputScriptOutcome Fail(string reason) => new(null, reason);
    }

    private static async Task<InputScriptOutcome> TryEvaluateInputScriptAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode targetNode,
        Uri inputRef,
        LogicNodeScriptHost scriptHost,
        IArtifactStore artifactStore)
    {
        if (string.IsNullOrWhiteSpace(targetNode.InputScript))
        {
            return InputScriptOutcome.Ok(inputRef);
        }

        var contextInputs = DeserializeContextInputs(saga.InputsJson);
        var workflowInputs = DeserializeContextInputs(saga.WorkflowInputsJson);
        var artifactJson = await ReadArtifactAsJsonAsync(
            artifactStore,
            inputRef,
            context.CancellationToken);

        var eval = scriptHost.Evaluate(
            workflowKey: workflow.Key,
            workflowVersion: workflow.Version,
            nodeId: targetNode.Id,
            script: targetNode.InputScript!,
            declaredPorts: targetNode.OutputPorts,
            input: artifactJson,
            context: contextInputs,
            cancellationToken: context.CancellationToken,
            workflow: workflowInputs,
            reviewRound: saga.ParentReviewRound,
            reviewMaxRounds: saga.ParentReviewMaxRounds,
            allowInputOverride: true,
            requireSetNodePath: false);

        saga.AppendLogicEvaluation(new LogicEvaluationRecord(
            NodeId: targetNode.Id,
            OutputPortName: eval.OutputPortName,
            RoundId: saga.CurrentRoundId,
            Duration: eval.Duration,
            Logs: eval.LogEntries,
            FailureKind: eval.Failure?.ToString(),
            FailureMessage: eval.FailureMessage,
            RecordedAtUtc: DateTime.UtcNow));

        if (eval.Failure is not null)
        {
            return InputScriptOutcome.Fail(
                $"Input script for node {targetNode.Id} failed ({eval.Failure}): {eval.FailureMessage}");
        }

        ApplyScriptUpdates(saga, contextInputs, workflowInputs, eval);

        if (!string.IsNullOrEmpty(eval.InputOverride))
        {
            var overrideRef = await WriteOverrideArtifactAsync(
                artifactStore,
                saga,
                targetNode.AgentKey,
                eval.InputOverride!,
                context.CancellationToken,
                fileNameSuffix: "scripted-input");
            return InputScriptOutcome.Ok(overrideRef);
        }

        return InputScriptOutcome.Ok(inputRef);
    }

    private static async Task<DecisionOutputTemplateOutcome> TryApplyDecisionOutputTemplateAsync(
        BehaviorContext<WorkflowSagaStateEntity, AgentInvocationCompleted> context,
        IAgentConfigRepository agentConfigRepo,
        IArtifactStore artifactStore,
        Runtime.IScribanTemplateRenderer templateRenderer,
        WorkflowSagaStateEntity saga,
        AgentInvocationCompleted message,
        string effectivePortName,
        Uri effectiveOutputRef)
    {
        AgentConfig agentConfig;
        try
        {
            agentConfig = await agentConfigRepo.GetAsync(
                message.AgentKey!,
                message.AgentVersion,
                context.CancellationToken);
        }
        catch (AgentConfigNotFoundException)
        {
            return DecisionOutputTemplateOutcome.None;
        }

        var template = ResolveDecisionOutputTemplate(
            agentConfig.Configuration.DecisionOutputTemplates,
            effectivePortName);

        if (template is null)
        {
            return DecisionOutputTemplateOutcome.None;
        }

        var outputJson = await ReadArtifactAsJsonAsync(
            artifactStore,
            effectiveOutputRef,
            context.CancellationToken);
        var outputText = TryReadOutputText(outputJson);

        JsonElement? inputJson = null;
        if (!string.IsNullOrWhiteSpace(saga.CurrentInputRef)
            && Uri.TryCreate(saga.CurrentInputRef, UriKind.Absolute, out var inputRef))
        {
            inputJson = await ReadArtifactAsJsonAsync(
                artifactStore,
                inputRef,
                context.CancellationToken);
        }

        var contextInputs = DeserializeContextInputs(saga.InputsJson);
        var workflowInputs = DeserializeContextInputs(saga.WorkflowInputsJson);
        var decisionName = message.OutputPortName ?? string.Empty;

        var scope = DecisionOutputTemplateContext.Build(
            decision: decisionName,
            outputPortName: effectivePortName,
            outputText: outputText,
            outputJson: IsStructured(outputJson) ? outputJson : null,
            inputJson: inputJson,
            contextInputs: contextInputs,
            workflowInputs: workflowInputs);

        string rendered;
        try
        {
            rendered = templateRenderer.Render(template, scope, context.CancellationToken);
        }
        catch (Runtime.PromptTemplateException ex)
        {
            return new DecisionOutputTemplateOutcome(null, $"Decision output template failed: {ex.Message}");
        }

        var overrideRef = await WriteOverrideArtifactAsync(
            artifactStore,
            saga,
            message.AgentKey,
            rendered,
            context.CancellationToken);

        return new DecisionOutputTemplateOutcome(overrideRef, null);
    }

    private static string? ResolveDecisionOutputTemplate(
        IReadOnlyDictionary<string, string>? templates,
        string portName)
    {
        if (templates is null || templates.Count == 0)
        {
            return null;
        }

        foreach (var entry in templates)
        {
            if (string.Equals(entry.Key, portName, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        if (templates.TryGetValue("*", out var wildcard))
        {
            return wildcard;
        }

        return null;
    }

    private static bool IsStructured(JsonElement element)
    {
        return element.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
    }

    private static string TryReadOutputText(JsonElement element)
    {
        // ReadArtifactAsJsonAsync wraps non-JSON text as { "text": "…" } — pull it back out so
        // `output` in the template context stays the raw submission as the author wrote it.
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("text", out var textProp)
            && textProp.ValueKind == JsonValueKind.String
            && element.EnumerateObject().Count() == 1)
        {
            return textProp.GetString() ?? string.Empty;
        }

        return element.GetRawText();
    }

    private readonly record struct DecisionOutputTemplateOutcome(Uri? OverrideOutputRef, string? FailureReason)
    {
        public static readonly DecisionOutputTemplateOutcome None = new(null, null);
    }

    /// <summary>
    /// Slice 13: merge an agent's pending <c>setContext</c> / <c>setWorkflow</c> writes (carried
    /// on <see cref="AgentInvocationCompleted"/>) into the saga's local-context and
    /// workflow-context bags. Mirrors <see cref="ApplyScriptUpdates"/> for Logic nodes — same
    /// merge semantics (last-write-wins per top-level key). No-op when the message has no
    /// updates.
    /// </summary>
    private static void ApplyAgentBagWrites(WorkflowSagaStateEntity saga, AgentInvocationCompleted message)
    {
        if (message.ContextUpdates is { Count: > 0 } contextUpdates)
        {
            var currentLocal = DeserializeContextInputs(saga.InputsJson);
            var mergedLocal = new Dictionary<string, JsonElement>(currentLocal, StringComparer.Ordinal);
            foreach (var (key, value) in contextUpdates)
            {
                mergedLocal[key] = value;
            }
            saga.InputsJson = SerializeContextInputs(mergedLocal);
        }

        if (message.WorkflowUpdates is { Count: > 0 } workflowUpdates)
        {
            var currentWorkflow = DeserializeContextInputs(saga.WorkflowInputsJson);
            var mergedWorkflow = new Dictionary<string, JsonElement>(currentWorkflow, StringComparer.Ordinal);
            foreach (var (key, value) in workflowUpdates)
            {
                mergedWorkflow[key] = value;
            }
            saga.WorkflowInputsJson = SerializeContextInputs(mergedWorkflow);
        }
    }

    private static void ApplyScriptUpdates(
        WorkflowSagaStateEntity saga,
        IReadOnlyDictionary<string, JsonElement> currentLocal,
        IReadOnlyDictionary<string, JsonElement> currentWorkflow,
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

        if (eval.WorkflowUpdates.Count > 0)
        {
            var mergedWorkflow = new Dictionary<string, JsonElement>(currentWorkflow, StringComparer.Ordinal);
            foreach (var (key, value) in eval.WorkflowUpdates)
            {
                mergedWorkflow[key] = value;
            }
            saga.WorkflowInputsJson = SerializeContextInputs(mergedWorkflow);
        }
    }

    private static JsonElement ComposeAgentScriptInput(
        JsonElement artifactJson,
        string decisionPortName,
        JsonElement? decisionPayload)
    {
        var decisionKindText = decisionPortName;
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
        var workflowInputs = DeserializeContextInputs(saga.WorkflowInputsJson);

        for (var hops = 0; hops < MaxLogicChainHops; hops++)
        {
            if (!inputLoaded)
            {
                inputJson = await ReadArtifactAsJsonAsync(artifactStore, upstreamOutputRef, context.CancellationToken);
                inputLoaded = true;
            }

            if (string.IsNullOrWhiteSpace(currentNode.OutputScript))
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
                script: currentNode.OutputScript,
                declaredPorts: currentNode.OutputPorts,
                input: inputJson,
                context: contextInputs,
                cancellationToken: context.CancellationToken,
                workflow: workflowInputs,
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

            ApplyScriptUpdates(saga, contextInputs, workflowInputs, eval);
            if (eval.ContextUpdates.Count > 0)
            {
                var merged = new Dictionary<string, JsonElement>(contextInputs, StringComparer.Ordinal);
                foreach (var (key, value) in eval.ContextUpdates)
                {
                    merged[key] = value;
                }
                contextInputs = merged;
            }
            if (eval.WorkflowUpdates.Count > 0)
            {
                var merged = new Dictionary<string, JsonElement>(workflowInputs, StringComparer.Ordinal);
                foreach (var (key, value) in eval.WorkflowUpdates)
                {
                    merged[key] = value;
                }
                workflowInputs = merged;
            }

            var chosenPort = eval.IsSuccess
                ? eval.OutputPortName!
                : ImplicitFailedPort;

            var nextEdge = workflow.FindNext(currentNode.Id, chosenPort);
            if (nextEdge is null)
            {
                saga.FailureReason = eval.IsSuccess
                    ? $"Logic node {currentNode.Id} emitted port '{chosenPort}' but no outgoing edge is connected."
                    : $"Logic node {currentNode.Id} failed ({eval.Failure}) and has no '{ImplicitFailedPort}' edge.";
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

    private static Task DispatchToNodeAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        IAgentConfigRepository agentConfigRepo,
        LogicNodeScriptHost scriptHost,
        IArtifactStore artifactStore,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode node,
        Uri inputRef,
        Guid roundId,
        CodeFlow.Contracts.RetryContext? retryContext)
    {
        return node.Kind switch
        {
            WorkflowNodeKind.Agent or WorkflowNodeKind.Hitl or WorkflowNodeKind.Start =>
                PublishHandoffAsync(context, agentConfigRepo, scriptHost, artifactStore, saga, workflow, node, inputRef, roundId, retryContext),
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

        var workflowContext = DeserializeContextInputs(saga.WorkflowInputsJson);

        return context.Publish(new SubflowInvokeRequested(
            ParentTraceId: saga.TraceId,
            ParentNodeId: subflowNode.Id,
            ParentRoundId: roundId,
            ChildTraceId: Guid.NewGuid(),
            SubflowKey: subflowNode.SubflowKey,
            SubflowVersion: subflowVersion,
            InputRef: inputRef,
            WorkflowContext: workflowContext,
            Depth: saga.SubflowDepth + 1,
            ReviewRound: reviewRound,
            ReviewMaxRounds: reviewMaxRounds,
            LoopDecision: loopDecision));
    }

    private static async Task PublishHandoffAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        IAgentConfigRepository agentConfigRepo,
        LogicNodeScriptHost scriptHost,
        IArtifactStore artifactStore,
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

        var inputOutcome = await TryEvaluateInputScriptAsync(
            context, saga, workflow, targetNode, inputRef, scriptHost, artifactStore);

        if (inputOutcome.Failed)
        {
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason = inputOutcome.FailureReason;
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        var effectiveInputRef = inputOutcome.InputRef!;

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

        saga.CurrentInputRef = effectiveInputRef.ToString();

        await context.Publish(new AgentInvokeRequested(
            TraceId: saga.TraceId,
            RoundId: roundId,
            WorkflowKey: saga.WorkflowKey,
            WorkflowVersion: saga.WorkflowVersion,
            NodeId: targetNode.Id,
            AgentKey: targetAgentKey,
            AgentVersion: pinnedVersion.Value,
            InputRef: effectiveInputRef,
            ContextInputs: DeserializeContextInputs(saga.InputsJson),
            RetryContext: retryContext,
            WorkflowContext: DeserializeContextInputs(saga.WorkflowInputsJson),
            ReviewRound: saga.ParentReviewRound,
            ReviewMaxRounds: saga.ParentReviewMaxRounds));
    }

    private static CodeFlow.Contracts.RetryContext? BuildRetryContextForHandoff(
        WorkflowSagaStateEntity saga,
        AgentInvocationCompleted message)
    {
        if (!string.Equals(message.OutputPortName, "Failed", StringComparison.Ordinal))
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
            && string.Equals(record.Decision, "Failed", StringComparison.Ordinal));
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
