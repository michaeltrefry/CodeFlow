using CodeFlow.Contracts;
using CodeFlow.Orchestration.Scripting;
using System.Diagnostics;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Observability;
using CodeFlow.Runtime.Container;
using CodeFlow.Runtime.Workspace;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace CodeFlow.Orchestration;

public sealed partial class WorkflowSagaStateMachine : MassTransitStateMachine<WorkflowSagaStateEntity>
{
    public const string PendingTransitionCompleted = "Completed";
    public const string PendingTransitionFailed = "Failed";

    /// <summary>
    /// Implicit error sink port name. Every node implicitly carries this port; if wired, the
    /// saga routes runtime errors and explicit <c>fail</c> tool calls down it. If unwired, the
    /// saga terminates with <c>FailureReason</c> set.
    /// </summary>
    public const string ImplicitFailedPort = "Failed";

    /// <summary>
    /// Synthesized success port emitted by a Transform node after its template renders. Authors
    /// don't declare it in <c>OutputPorts</c>; the saga and validator both reference this name
    /// when wiring outgoing edges from Transform nodes.
    /// </summary>
    public const string TransformOutputPort = "Out";

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
        WhenEnter(Completed, binder => binder.ThenAsync(TryCleanupWorkflowContainersAsync));
        WhenEnter(Completed, binder => binder.ThenAsync(TryCleanupHappyPathWorkdirAsync));
        WhenEnter(Failed, binder => binder.ThenAsync(context => PublishSubflowCompletedIfChildAsync(context, "Failed")));
        WhenEnter(Failed, binder => binder.ThenAsync(TryCleanupWorkflowContainersAsync));
    }

    private static async Task TryCleanupWorkflowContainersAsync(
        BehaviorContext<WorkflowSagaStateEntity> context)
    {
        var services = context.GetPayload<IServiceProvider>();
        var lifecycle = services.GetService<DockerLifecycleService>();
        if (lifecycle is null)
        {
            return;
        }

        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<WorkflowSagaStateMachine>();

        try
        {
            var cleanup = await lifecycle.CleanupWorkflowAsync(context.Saga.TraceId, context.CancellationToken);
            if (cleanup.RemovedContainers > 0
                || cleanup.RemovedVolumes > 0
                || cleanup.RemovedExecutionWorkspaces > 0)
            {
                logger.LogInformation(
                    "Cleaned up {ContainerCount} container(s), {VolumeCount} cache volume(s), and {WorkspaceCount} execution workspace(s) for workflow trace {TraceId}.",
                    cleanup.RemovedContainers,
                    cleanup.RemovedVolumes,
                    cleanup.RemovedExecutionWorkspaces,
                    context.Saga.TraceId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to clean up workflow-scoped Docker resources for trace {TraceId}; orphan sweep should retry later.",
                context.Saga.TraceId);
        }
    }

    /// <summary>
    /// Happy-path workdir cleanup. Fires when a top-level saga reaches <see cref="Completed"/>
    /// AND every entry in <c>workflow.repositories</c> has a non-empty <c>prUrl</c> (set by the
    /// publish agent via <c>setWorkflow</c>). If either condition fails — child saga, no
    /// repositories array, or any repo missing a PR URL — the workdir is left in place so an
    /// operator can inspect what went wrong. Slice F's periodic sweep catches anything that's
    /// genuinely orphaned past the configured TTL. sc-607: this read used to come off
    /// <c>saga.InputsJson</c> (context.*) but moved to <c>saga.WorkflowInputsJson</c> when the
    /// repos convention shifted to the workflow-context bag.
    /// </summary>
    private static Task TryCleanupHappyPathWorkdirAsync(
        BehaviorContext<WorkflowSagaStateEntity> context)
    {
        var saga = context.Saga;

        // Subflow children share the parent's workdir — cleanup happens once at the top level.
        if (saga.ParentTraceId is not null)
        {
            return Task.CompletedTask;
        }

        if (!AllRepositoriesHavePrUrl(saga.WorkflowInputsJson))
        {
            return Task.CompletedTask;
        }

        var services = context.GetPayload<IServiceProvider>();
        var workspaceOptions = services.GetRequiredService<IOptions<WorkspaceOptions>>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        TraceWorkdirCleanup.TryRemove(
            workspaceOptions.Value.WorkingDirectoryRoot,
            saga.TraceId,
            loggerFactory.CreateLogger<WorkflowSagaStateMachine>());

        return Task.CompletedTask;
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
        saga.CurrentRoundEnteredAtUtc = nowUtc;
        saga.RoundCount = 0;
        saga.InputsJson = SerializeContextInputs(message.ContextInputs);
        if (message.WorkflowContext is not null)
        {
            saga.WorkflowInputsJson = SerializeContextInputs(message.WorkflowContext);
        }
        saga.CurrentInputRef = message.InputRef?.ToString();
        saga.PinAgentVersion(message.AgentKey, message.AgentVersion);
        // Seed the per-trace repository allowlist. sc-607: lifted from the workflow-context bag
        // (workflow.repositories), not the local-context bag — repositories are trace-tree state
        // and belong on the bag that propagates through subflows. TracesEndpoints routes the
        // workflow-input convention's value into WorkflowContext at launch so authors can keep
        // declaring `repositories` as a Json workflow input.
        saga.RepositoriesJson = LiftRepositoriesFromWorkflowBag(message.WorkflowContext)
            ?? saga.RepositoriesJson;
        // sc-593 Phase 1: seed the per-trace working directory from the new contract field.
        // TracesEndpoints.CreateTraceAsync populates message.TraceWorkDir starting in Phase 2
        // (sc-602); until that lands we accept the legacy `workflow.workDir` bag-key as a
        // fallback so sagas still get a workspace anchor on day-one. Phase 3 (sc-604) drops
        // the fallback once in-flight messages have drained.
        if (!string.IsNullOrWhiteSpace(message.TraceWorkDir))
        {
            saga.TraceWorkDir = message.TraceWorkDir;
        }
        else if (TryGetWorkflowWorkDirFromContext(message.WorkflowContext, out var legacyWorkDir))
        {
            saga.TraceWorkDir = legacyWorkDir;
        }
        if (saga.CreatedAtUtc == default)
        {
            saga.CreatedAtUtc = nowUtc;
        }
        saga.UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// sc-593 Phase 1 transitional helper: read the legacy <c>workflow.workDir</c> bag-key from
    /// a workflow-context dictionary so <see cref="ApplyInitialRequest"/> can seed
    /// <see cref="WorkflowSagaStateEntity.TraceWorkDir"/> from a launch message that hasn't been
    /// upgraded yet (Phase 2's sc-602 wires <c>TracesEndpoints</c> to set the new field directly).
    /// Mirrors the shape of <c>AgentInvocationConsumer.TryGetWorkflowWorkDir</c> but lives here
    /// because the saga sees the bag at saga-init time, before the consumer ever runs. Removed
    /// in Phase 3 (sc-604) once the bag entry is no longer seeded.
    /// </summary>
    private static bool TryGetWorkflowWorkDirFromContext(
        IReadOnlyDictionary<string, JsonElement>? workflowContext,
        out string workDir)
    {
        workDir = string.Empty;
        if (workflowContext is null
            || !workflowContext.TryGetValue("workDir", out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        workDir = value;
        return true;
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
        saga.CurrentRoundEnteredAtUtc = nowUtc;
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
        // Inherit the parent's per-trace repository allowlist so vcs_* tools inside this subflow
        // see the same allowed repos. Without this hand-off the child's local context starts
        // empty (line above) and every vcs_* call would return repo_not_allowed even though the
        // parent declared the repos.
        saga.RepositoriesJson = SerializeRepositories(message.Repositories);
        // sc-593: subflows deliberately share the parent's workspace, so we copy the path
        // verbatim. NOT computed from message.ChildTraceId — that would give every subflow its
        // own subdirectory and break code-aware tools that operate on a single repo checkout.
        saga.TraceWorkDir = message.TraceWorkDir;
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
            ReviewMaxRounds: message.ReviewMaxRounds,
            Repositories: ParseRepositoriesJson(saga.RepositoriesJson),
            TraceWorkDir: saga.TraceWorkDir));
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
            TerminalPort: saga.LastEffectivePort,
            FailureReason: string.Equals(terminalPortName, ImplicitFailedPort, StringComparison.Ordinal)
                ? saga.FailureReason
                : null));
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

                await AccumulateRejectionHistoryAsync(
                    saga,
                    parentNode,
                    message,
                    artifactStore,
                    context.CancellationToken);

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
            OutputRef: message.OutputRef.ToString(),
            NodeEnteredAtUtc: saga.CurrentRoundEnteredAtUtc));

        // Mirror the agent-completion path: when the child's effective port is Failed, lift its
        // saga-level FailureReason onto the parent before edge lookup, so the unwired-Failed-port
        // branch below doesn't bury the underlying cause behind a generic "no outgoing edge" string.
        if (string.Equals(effectivePortName, ImplicitFailedPort, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(message.FailureReason))
        {
            saga.FailureReason ??= message.FailureReason;
        }

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

        var dispatchInputRef = resolution.OverrideInputRef ?? message.OutputRef;

        if (resolution is { CleanlyCompleted: true })
        {
            saga.LastEffectivePort = resolution.CleanlyCompletedPort ?? TransformOutputPort;
            saga.PendingTransition = PendingTransitionCompleted;
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
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
            inputRef: dispatchInputRef,
            roundId: targetRoundId,
            retryContext: null);

        var nowUtc = DateTime.UtcNow;
        saga.CurrentNodeId = targetNode.Id;
        saga.CurrentAgentKey = targetNode.AgentKey ?? string.Empty;
        saga.CurrentRoundId = targetRoundId;
        saga.CurrentRoundEnteredAtUtc = nowUtc;
        saga.RoundCount = targetRoundCount;
        saga.UpdatedAtUtc = nowUtc;

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
        // otherwise mutate the saga and route to the wrong node. Coordinator-protocol Swarm
        // dispatch (sc-46) carves out a narrow exception: parallel worker rounds tracked in the
        // saga's pending set are accepted even when CurrentRoundId points at a sibling worker.
        if (message.RoundId != saga.CurrentRoundId
            && !IsAcceptablePendingParallelRound(saga, message))
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

        // sc-43 / sc-46: when the saga is inside a Swarm node, route the completion through the
        // swarm-internal flow. Sequential contributors advance position-by-position; the
        // Coordinator's coordinator agent dispatches N parallel workers; parallel workers fold
        // back into the contributions array as they complete. Any of those return early (the
        // dispatched message has been handled internally). The synthesizer's completion (and any
        // Failed-port completion from a contributor / coordinator / worker) falls through to the
        // normal port-routing flow below: the saga's swarm state gets cleared so the swarm node
        // looks like any other agent-bearing node from there on.
        if (saga.CurrentSwarmNodeId is Guid swarmNodeId
            && swarmNodeId == message.FromNodeId)
        {
            var swarmNode = workflow.FindNode(swarmNodeId);
            var isFailedPort = string.Equals(message.OutputPortName, ImplicitFailedPort, StringComparison.Ordinal);

            if (swarmNode is { Kind: WorkflowNodeKind.Swarm } && !isFailedPort)
            {
                var isCoordinator = IsSwarmCoordinatorCompletion(swarmNode, message);
                var isContributor = IsSwarmContributorCompletion(swarmNode, message);
                var isParallelDispatchActive = IsCoordinatorParallelDispatchActive(saga);

                string? swarmFailureReason = null;

                if (isCoordinator)
                {
                    swarmFailureReason = await HandleSwarmCoordinatorCompletionAsync(
                        context,
                        agentConfigRepo,
                        artifactStore,
                        saga,
                        workflow,
                        swarmNode,
                        message);
                }
                else if (isContributor && isParallelDispatchActive)
                {
                    swarmFailureReason = await HandleSwarmWorkerCompletionAsync(
                        context,
                        agentConfigRepo,
                        artifactStore,
                        saga,
                        workflow,
                        swarmNode,
                        message);
                }
                else if (isContributor)
                {
                    swarmFailureReason = await HandleSwarmContributorCompletionAsync(
                        context,
                        agentConfigRepo,
                        artifactStore,
                        saga,
                        workflow,
                        swarmNode,
                        message);
                }

                if (isCoordinator || isContributor)
                {
                    if (swarmFailureReason is not null)
                    {
                        saga.PendingTransition = PendingTransitionFailed;
                        saga.FailureReason = swarmFailureReason;
                        saga.UpdatedAtUtc = DateTime.UtcNow;
                        ClearSwarmState(saga);
                    }
                    return;
                }
            }

            // Synthesizer completion or any Failed-port completion: clear swarm state so the
            // normal flow below routes the swarm node's outgoing edges based on the message's
            // port name. For synthesizers that's the synthesized port; for failed coordinators /
            // contributors / workers it's the Failed port the swarm node terminates on.
            ClearSwarmState(saga);
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

        // Decision output template: if no setOutput() override was applied and the agent declares
        // a matching per-decision template, render it server-side and substitute the effective
        // output ref. Script overrides always win so authors have an explicit escape hatch.
        string? decisionTemplateFailure = null;
        if (portResolution.OverrideOutputRef is null
            && effectiveOutputRef is not null
            && !string.IsNullOrWhiteSpace(message.AgentKey))
        {
            var decisionRenderer = services.GetRequiredService<IDecisionTemplateRenderer>();
            var templateOutcome = await TryApplyDecisionOutputTemplateAsync(
                context,
                agentConfigRepo,
                artifactStore,
                decisionRenderer,
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
            OutputRef: effectiveOutputRef?.ToString(),
            NodeEnteredAtUtc: saga.CurrentRoundEnteredAtUtc));

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

        // When the agent submitted a Failed decision, lift its reason onto the saga *before* the
        // edge lookup. Otherwise the unwired-Failed-port branch below would stamp a generic "no
        // outgoing edge" message that hides the real cause, which is buried in DecisionPayload.
        if (string.Equals(effectivePortName, ImplicitFailedPort, StringComparison.Ordinal))
        {
            var (agentReason, _) = ExtractFailureContext(message.DecisionPayload);
            if (!string.IsNullOrWhiteSpace(agentReason))
            {
                saga.FailureReason ??= agentReason;
            }
        }

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

        // A Transform node in the chain may have rewritten the artifact. The dispatched node
        // (and the saga's CurrentInputRef in PublishHandoffAsync / PublishSubflowDispatchAsync)
        // must see the rendered ref, not the original agent output ref.
        var dispatchInputRef = resolution.OverrideInputRef ?? effectiveOutputRef;

        if (resolution is { CleanlyCompleted: true })
        {
            // Transform with no edge from "Out" — clean workflow termination with the rendered
            // artifact as the final output. Mirrors the unwired-author-port rule above for Agent
            // terminals; the terminal port is "Out" so a parent Subflow node can route from it.
            saga.LastEffectivePort = resolution.CleanlyCompletedPort ?? TransformOutputPort;
            saga.PendingTransition = PendingTransitionCompleted;
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        var targetNode = resolution.TerminalNode!;
        var targetRoundId = resolution.RotatesRound ? Guid.NewGuid() : saga.CurrentRoundId;
        var targetRoundCount = resolution.RotatesRound ? 0 : saga.RoundCount + 1;
        var retryContextBuilder = services.GetRequiredService<IRetryContextBuilder>();
        var retryContext = BuildRetryContextForHandoff(retryContextBuilder, saga, message);

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
            inputRef: dispatchInputRef,
            roundId: targetRoundId,
            retryContext: retryContext);

        var nowUtc = DateTime.UtcNow;
        saga.CurrentNodeId = targetNode.Id;
        saga.CurrentAgentKey = targetNode.AgentKey ?? string.Empty;
        saga.CurrentRoundId = targetRoundId;
        saga.CurrentRoundEnteredAtUtc = nowUtc;
        saga.RoundCount = targetRoundCount;
        saga.UpdatedAtUtc = nowUtc;

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
        if (fromNode is null)
        {
            return new SourcePortResolution(fallbackPort, null);
        }

        // Logic nodes are resolved through ResolveTargetThroughLogicChainAsync before any
        // AgentInvokeRequested is published, so a Logic node's id should never reach this
        // method as the source of an AgentInvocationCompleted. The defensive branch was
        // removed in F-023; a debug assertion preserves the belt-and-braces signal.
        Debug.Assert(
            fromNode.Kind != WorkflowNodeKind.Logic,
            $"AgentInvocationCompleted carried Logic node {fromNode.Id} as FromNodeId — Logic nodes should be resolved before publish.");

        // P4: mirror the agent's output text into the configured workflow variable BEFORE the
        // output script runs so the script can read `workflow[mirrorKey]`. Even if the node has
        // no output script, mirroring still applies — the feature is independent.
        var mirrorTarget = AgentOutputTransforms.NormalizeMirrorTarget(fromNode.MirrorOutputToWorkflowVar);
        var hasOutputScript = !string.IsNullOrWhiteSpace(fromNode.OutputScript);
        var portReplacementsByPort = AgentOutputTransforms.NormalizePortReplacements(fromNode.OutputPortReplacements);

        if (mirrorTarget is null && !hasOutputScript && portReplacementsByPort is null)
        {
            return new SourcePortResolution(fallbackPort, null);
        }

        if (message.OutputRef is null)
        {
            return new SourcePortResolution(fallbackPort, null);
        }

        string? artifactText = null;
        if (mirrorTarget is not null || hasOutputScript)
        {
            artifactText = await ReadArtifactAsTextAsync(
                artifactStore,
                message.OutputRef,
                context.CancellationToken);
        }

        if (mirrorTarget is not null && artifactText is not null)
        {
            ApplyMirrorOutputToWorkflow(saga, mirrorTarget, artifactText);
        }

        var contextInputs = DeserializeContextInputs(saga.InputsJson);
        var workflowInputs = DeserializeContextInputs(saga.WorkflowInputsJson);

        if (!hasOutputScript)
        {
            // P5 may still apply on the no-script path. Use the agent's port verbatim as the
            // routing decision and apply any binding for that port.
            var directOverride = portReplacementsByPort is not null
                ? await TryApplyPortReplacementAsync(
                    portReplacementsByPort,
                    fallbackPort,
                    workflowInputs,
                    artifactStore,
                    saga,
                    fromNode.AgentKey,
                    context.CancellationToken)
                : null;
            return new SourcePortResolution(fallbackPort, directOverride);
        }

        var artifactJson = ParseArtifactAsJson(artifactText!);
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

        var resolvedPort = eval.OutputPortName!;

        // P5: per-port binding takes precedence over setOutput so authors who want both
        // a script-managed variable AND a per-port artifact replacement get the binding's
        // value. The setOutput value is still side-effected via WriteOverrideArtifact when
        // no binding fires for the resolved port.
        if (portReplacementsByPort is not null)
        {
            var refreshedWorkflowInputs = DeserializeContextInputs(saga.WorkflowInputsJson);
            var bindingOverride = await TryApplyPortReplacementAsync(
                portReplacementsByPort,
                resolvedPort,
                refreshedWorkflowInputs,
                artifactStore,
                saga,
                fromNode.AgentKey,
                context.CancellationToken);
            if (bindingOverride is not null)
            {
                return new SourcePortResolution(resolvedPort, bindingOverride);
            }
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

        return new SourcePortResolution(resolvedPort, overrideRef);
    }

    private static void ApplyMirrorOutputToWorkflow(
        WorkflowSagaStateEntity saga,
        string mirrorKey,
        string artifactText)
    {
        var current = DeserializeContextInputs(saga.WorkflowInputsJson);
        var mirrored = AgentOutputTransforms.Mirror(current, mirrorKey, artifactText);
        saga.WorkflowInputsJson = SerializeContextInputs(mirrored);
    }

    private static async Task<Uri?> TryApplyPortReplacementAsync(
        IReadOnlyDictionary<string, string> portReplacementsByPort,
        string? resolvedPort,
        IReadOnlyDictionary<string, JsonElement> workflowInputs,
        IArtifactStore artifactStore,
        WorkflowSagaStateEntity saga,
        string? agentKey,
        CancellationToken cancellationToken)
    {
        var replacementText = AgentOutputTransforms.TryGetPortReplacement(
            portReplacementsByPort,
            resolvedPort,
            workflowInputs);

        if (replacementText is null)
        {
            return null;
        }

        return await WriteOverrideArtifactAsync(
            artifactStore,
            saga,
            agentKey,
            replacementText,
            cancellationToken,
            fileNameSuffix: "port-replacement");
    }

    private static async Task<string> ReadArtifactAsTextAsync(
        IArtifactStore artifactStore,
        Uri outputRef,
        CancellationToken cancellationToken)
    {
        await using var stream = await artifactStore.ReadAsync(outputRef, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static JsonElement ParseArtifactAsJson(string text)
    {
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
            // Upstream agent produced plain text — expose as { "text": "…" } so scripts can
            // still read it (mirrors ReadArtifactAsJsonAsync's fallback).
            var doc = new { text };
            return JsonSerializer.SerializeToElement(doc);
        }
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
        IDecisionTemplateRenderer decisionRenderer,
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

        var inputs = new DecisionTemplateInputs(
            DecisionName: message.OutputPortName ?? string.Empty,
            EffectivePortName: effectivePortName,
            OutputText: outputText,
            OutputJson: outputJson,
            InputJson: inputJson,
            ContextInputs: DeserializeContextInputs(saga.InputsJson),
            WorkflowInputs: DeserializeContextInputs(saga.WorkflowInputsJson));

        var result = decisionRenderer.Render(agentConfig, inputs, context.CancellationToken);
        switch (result)
        {
            case DecisionTemplateRenderResult.Skipped:
                return DecisionOutputTemplateOutcome.None;
            case DecisionTemplateRenderResult.Failed failed:
                return new DecisionOutputTemplateOutcome(null, $"Decision output template failed: {failed.Reason}");
            case DecisionTemplateRenderResult.Rendered rendered:
                var overrideRef = await WriteOverrideArtifactAsync(
                    artifactStore,
                    saga,
                    message.AgentKey,
                    rendered.Text,
                    context.CancellationToken);
                return new DecisionOutputTemplateOutcome(overrideRef, null);
            default:
                throw new InvalidOperationException($"Unexpected render result: {result.GetType()}");
        }
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
            // sc-607: setWorkflow({repositories: [...]}) is the runtime mutation surface for the
            // per-trace allowlist. Lift the update to the saga field so it propagates to subflows.
            saga.RepositoriesJson = LiftRepositoriesFromWorkflowBag(mergedWorkflow)
                ?? saga.RepositoriesJson;
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
            // sc-607: a routing script that writes workflow.repositories must also update the
            // saga-level allowlist so it propagates to subflows. Parallels the agent-decision
            // path in ApplyAgentBagWrites.
            saga.RepositoriesJson = LiftRepositoriesFromWorkflowBag(mergedWorkflow)
                ?? saga.RepositoriesJson;
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

        if (currentNode.Kind != WorkflowNodeKind.Logic
            && currentNode.Kind != WorkflowNodeKind.Transform)
        {
            return new LogicChainResolution(currentNode, rotates, FailureTerminal: false);
        }

        JsonElement inputJson = default;
        var inputLoaded = false;
        var currentUpstreamRef = upstreamOutputRef;
        Uri? renderedRefSinceUpstream = null;
        var contextInputs = DeserializeContextInputs(saga.InputsJson);
        var workflowInputs = DeserializeContextInputs(saga.WorkflowInputsJson);
        var templateRenderer = context.GetPayload<IServiceProvider>()
            .GetRequiredService<Runtime.IScribanTemplateRenderer>();

        for (var hops = 0; hops < MaxLogicChainHops; hops++)
        {
            if (currentNode.Kind == WorkflowNodeKind.Transform)
            {
                var transformOutcome = await ExecuteTransformInChainAsync(
                    context,
                    saga,
                    workflow,
                    currentNode,
                    currentUpstreamRef,
                    contextInputs,
                    workflowInputs,
                    scriptHost,
                    templateRenderer,
                    artifactStore);

                if (transformOutcome.Failure is { } failure)
                {
                    saga.FailureReason ??= failure;
                    return new LogicChainResolution(null, rotates, FailureTerminal: true);
                }

                currentUpstreamRef = transformOutcome.OutputRef!;
                renderedRefSinceUpstream = transformOutcome.OutputRef;
                inputLoaded = false;

                // Transform's input/output scripts may have written context/workflow updates to
                // saga; refresh locals so any subsequent Logic node in the chain sees them.
                contextInputs = DeserializeContextInputs(saga.InputsJson);
                workflowInputs = DeserializeContextInputs(saga.WorkflowInputsJson);

                var transformEdge = workflow.FindNext(currentNode.Id, TransformOutputPort);
                if (transformEdge is null)
                {
                    // No edge from Transform's Out — terminate the saga cleanly with the rendered
                    // artifact as the final output, mirroring the unwired-author-port rule that
                    // applies to Agent terminals in RouteCompletionAsync.
                    return new LogicChainResolution(
                        TerminalNode: null,
                        RotatesRound: rotates,
                        FailureTerminal: false,
                        OverrideInputRef: currentUpstreamRef,
                        CleanlyCompleted: true,
                        CleanlyCompletedPort: TransformOutputPort);
                }

                if (transformEdge.RotatesRound)
                {
                    rotates = true;
                }

                currentNode = workflow.FindNode(transformEdge.ToNodeId)
                    ?? throw new InvalidOperationException(
                        $"Edge {transformEdge.FromNodeId}:{transformEdge.FromPort} → {transformEdge.ToNodeId} references a missing node.");

                if (currentNode.Kind != WorkflowNodeKind.Logic
                    && currentNode.Kind != WorkflowNodeKind.Transform)
                {
                    return new LogicChainResolution(
                        currentNode,
                        rotates,
                        FailureTerminal: false,
                        OverrideInputRef: renderedRefSinceUpstream);
                }

                continue;
            }

            if (!inputLoaded)
            {
                inputJson = await ReadArtifactAsJsonAsync(artifactStore, currentUpstreamRef, context.CancellationToken);
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

            if (currentNode.Kind != WorkflowNodeKind.Logic
                && currentNode.Kind != WorkflowNodeKind.Transform)
            {
                return new LogicChainResolution(
                    currentNode,
                    rotates,
                    FailureTerminal: false,
                    OverrideInputRef: renderedRefSinceUpstream);
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

    private readonly record struct TransformChainOutcome(Uri? OutputRef, string? Failure)
    {
        public static TransformChainOutcome Ok(Uri outputRef) => new(outputRef, null);
        public static TransformChainOutcome Fail(string reason) => new(null, reason);
    }

    /// <summary>
    /// Render a Transform node's template against <c>input.* / context.* / workflow.*</c> and
    /// persist the rendered text as a new artifact. Used by the inline chain resolver so a
    /// Transform that follows an Agent (or another Transform) reshapes the artifact without
    /// allocating a saga round. Render errors and template parse errors surface as
    /// <see cref="TransformChainOutcome.Failure"/>; the caller routes them to the implicit
    /// <see cref="ImplicitFailedPort"/>.
    /// </summary>
    private static async Task<TransformChainOutcome> ExecuteTransformInChainAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode transformNode,
        Uri inputRef,
        IReadOnlyDictionary<string, JsonElement> contextInputs,
        IReadOnlyDictionary<string, JsonElement> workflowInputs,
        LogicNodeScriptHost scriptHost,
        Runtime.IScribanTemplateRenderer templateRenderer,
        IArtifactStore artifactStore)
    {
        if (string.IsNullOrWhiteSpace(transformNode.Template))
        {
            return TransformChainOutcome.Fail(
                $"Transform node {transformNode.Id} has no template (workflow {workflow.Key} v{workflow.Version}).");
        }

        var effectiveInputRef = inputRef;
        var jsonMode = string.Equals(transformNode.OutputType, "json", StringComparison.Ordinal);

        // 1. inputScript: shape the structured input the template will see. Reuses the
        //    saga-wide setInput helper so the same setContext/setWorkflow/setInput semantics
        //    apply byte-for-byte as on Agent/HITL/Subflow nodes.
        if (!string.IsNullOrWhiteSpace(transformNode.InputScript))
        {
            var inputOutcome = await TryEvaluateInputScriptAsync(
                context,
                saga,
                workflow,
                transformNode,
                effectiveInputRef,
                scriptHost,
                artifactStore);

            if (inputOutcome.Failed)
            {
                return TransformChainOutcome.Fail(
                    $"Transform node {transformNode.Id} input script failed: {inputOutcome.FailureReason}");
            }

            effectiveInputRef = inputOutcome.InputRef!;

            // setContext/setWorkflow updates landed in saga.InputsJson / saga.WorkflowInputsJson
            // via ApplyScriptUpdates. The chain-resolver's locals are stale; refresh them so the
            // template scope below reflects what the input script just set.
            contextInputs = DeserializeContextInputs(saga.InputsJson);
            workflowInputs = DeserializeContextInputs(saga.WorkflowInputsJson);
        }

        // 2. Build template scope from the (possibly script-mutated) input + vars.
        JsonElement inputJson;
        try
        {
            inputJson = await ReadArtifactAsJsonAsync(artifactStore, effectiveInputRef, context.CancellationToken);
        }
        catch (Exception ex)
        {
            return TransformChainOutcome.Fail(
                $"Transform node {transformNode.Id} failed to read upstream artifact: {ex.Message}");
        }

        var scope = TransformNodeContext.Build(inputJson, contextInputs, workflowInputs);

        // 3. Render template.
        string rendered;
        try
        {
            rendered = templateRenderer.Render(transformNode.Template!, scope, context.CancellationToken);
        }
        catch (Runtime.PromptTemplateException ex)
        {
            return TransformChainOutcome.Fail(
                $"Transform node {transformNode.Id} template render failed: {ex.Message}");
        }

        // 4. JSON validation (TN-2).
        if (jsonMode)
        {
            try
            {
                using var _ = JsonDocument.Parse(rendered);
            }
            catch (JsonException ex)
            {
                return TransformChainOutcome.Fail(
                    $"Transform node {transformNode.Id} produced invalid JSON (outputType=json): {ex.Message}");
            }
        }

        // 5. outputScript: can mutate context/workflow vars and override the artifact text via
        //    setOutput(). In JSON mode the override is re-validated.
        var finalText = rendered;
        if (!string.IsNullOrWhiteSpace(transformNode.OutputScript))
        {
            var outputScriptOutcome = await ApplyTransformOutputScriptAsync(
                context,
                saga,
                workflow,
                transformNode,
                rendered,
                jsonMode,
                contextInputs,
                workflowInputs,
                scriptHost);

            if (outputScriptOutcome.Failure is { } failure)
            {
                return TransformChainOutcome.Fail(failure);
            }

            finalText = outputScriptOutcome.FinalText!;
        }

        // 6. Persist artifact.
        var outputRef = await WriteOverrideArtifactAsync(
            artifactStore,
            saga,
            agentKey: null,
            finalText,
            context.CancellationToken,
            fileNameSuffix: "transform-output");

        return TransformChainOutcome.Ok(outputRef);
    }

    private readonly record struct TransformOutputScriptOutcome(string? FinalText, string? Failure)
    {
        public static TransformOutputScriptOutcome Ok(string finalText) => new(finalText, null);
        public static TransformOutputScriptOutcome Fail(string reason) => new(null, reason);
    }

    /// <summary>
    /// Run a Transform node's <c>outputScript</c> after the template has rendered (and after JSON
    /// validation when <c>outputType=="json"</c>). The script sees:
    /// <list type="bullet">
    ///   <item><description><c>output</c> — the rendered text. In JSON mode it's the parsed
    ///     object/array so authors can do <c>output.foo</c>; in string mode it's a JS string.</description></item>
    ///   <item><description><c>context</c>, <c>workflow</c> — same frozen snapshots every other
    ///     script sees.</description></item>
    /// </list>
    /// Allowed mutations: <c>setContext</c>, <c>setWorkflow</c>, <c>setOutput(text)</c>. The
    /// <c>setOutput</c> override replaces the artifact text. In JSON mode the override is
    /// re-validated; an invalid JSON override surfaces as a chain failure.
    /// </summary>
    private static async Task<TransformOutputScriptOutcome> ApplyTransformOutputScriptAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode transformNode,
        string rendered,
        bool jsonMode,
        IReadOnlyDictionary<string, JsonElement> contextInputs,
        IReadOnlyDictionary<string, JsonElement> workflowInputs,
        LogicNodeScriptHost scriptHost)
    {
        JsonElement scriptInput;
        if (jsonMode)
        {
            // Already validated upstream; this Parse must succeed.
            using var doc = JsonDocument.Parse(rendered);
            scriptInput = doc.RootElement.Clone();
        }
        else
        {
            scriptInput = JsonSerializer.SerializeToElement(rendered);
        }

        var eval = scriptHost.Evaluate(
            workflowKey: workflow.Key,
            workflowVersion: workflow.Version,
            nodeId: transformNode.Id,
            script: transformNode.OutputScript!,
            declaredPorts: transformNode.OutputPorts,
            input: scriptInput,
            context: contextInputs,
            cancellationToken: context.CancellationToken,
            workflow: workflowInputs,
            reviewRound: saga.ParentReviewRound,
            reviewMaxRounds: saga.ParentReviewMaxRounds,
            allowOutputOverride: true,
            inputVariableName: "output",
            requireSetNodePath: false);

        saga.AppendLogicEvaluation(new LogicEvaluationRecord(
            NodeId: transformNode.Id,
            OutputPortName: eval.OutputPortName,
            RoundId: saga.CurrentRoundId,
            Duration: eval.Duration,
            Logs: eval.LogEntries,
            FailureKind: eval.Failure?.ToString(),
            FailureMessage: eval.FailureMessage,
            RecordedAtUtc: DateTime.UtcNow));

        if (eval.Failure is not null)
        {
            return TransformOutputScriptOutcome.Fail(
                $"Transform node {transformNode.Id} output script failed ({eval.Failure}): {eval.FailureMessage}");
        }

        ApplyScriptUpdates(saga, contextInputs, workflowInputs, eval);

        var finalText = string.IsNullOrEmpty(eval.OutputOverride) ? rendered : eval.OutputOverride!;

        if (jsonMode && eval.OutputOverride is not null)
        {
            try
            {
                using var _ = JsonDocument.Parse(finalText);
            }
            catch (JsonException ex)
            {
                return TransformOutputScriptOutcome.Fail(
                    $"Transform node {transformNode.Id} output script setOutput value is not valid JSON (outputType=json): {ex.Message}");
            }
        }

        await Task.CompletedTask; // helper is async-ready for future I/O paths; current body is sync.
        return TransformOutputScriptOutcome.Ok(finalText);
    }

    /// <summary>
    /// Outcome of walking the inline-node chain (Logic + Transform) starting from the edge that
    /// leaves the most-recently-completed node. Three mutually exclusive shapes:
    /// <list type="bullet">
    ///   <item><description><c>FailureTerminal</c> — the chain hit a misconfiguration or the
    ///     resolver synthesized <see cref="ImplicitFailedPort"/>. Caller must read
    ///     <c>saga.FailureReason</c> and transition the saga to Failed.</description></item>
    ///   <item><description><c>CleanlyCompleted</c> — a Transform node rendered successfully but
    ///     its <c>Out</c> port has no outgoing edge. Caller terminates the saga as Completed with
    ///     <see cref="OverrideInputRef"/> as the final artifact, mirroring the unwired-author-port
    ///     rule for Agent terminals.</description></item>
    ///   <item><description>Otherwise — <see cref="TerminalNode"/> is the next dispatchable node;
    ///     <see cref="OverrideInputRef"/>, when non-null, replaces the upstream output ref because
    ///     a Transform node in the chain rewrote the artifact.</description></item>
    /// </list>
    /// </summary>
    private sealed record LogicChainResolution(
        WorkflowNode? TerminalNode,
        bool RotatesRound,
        bool FailureTerminal,
        Uri? OverrideInputRef = null,
        bool CleanlyCompleted = false,
        string? CleanlyCompletedPort = null);

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
            // Upstream agent produced plain text — expose as { "text": "…" } so scripts can still let it.
            var doc = new { text };
            return JsonSerializer.SerializeToElement(doc);
        }
    }

    /// <summary>
    /// P3: when a ReviewLoop's reviewer rejects on a non-final round, append the loop-decision
    /// artifact to the framework-managed <c>__loop.rejectionHistory</c> workflow variable so
    /// the next round's child workflow renders it via the auto-injected
    /// <c>{{ rejectionHistory }}</c> binding. No-op when the parent ReviewLoop has the feature
    /// disabled (NULL config, or <c>Enabled=false</c>) — that's the migration-safe default.
    /// </summary>
    private static async Task AccumulateRejectionHistoryAsync(
        WorkflowSagaStateEntity saga,
        WorkflowNode parentNode,
        SubflowCompleted message,
        IArtifactStore artifactStore,
        CancellationToken cancellationToken)
    {
        var config = parentNode.RejectionHistory;
        if (config is null || !config.Enabled)
        {
            return;
        }

        string artifactBody;
        try
        {
            await using var stream = await artifactStore.ReadAsync(message.OutputRef, cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
            artifactBody = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Accumulation is a best-effort enrichment — never fail the loop because the
            // history couldn't be appended. The next round simply runs without the new entry.
            return;
        }

        var workflowBag = new Dictionary<string, JsonElement>(
            DeserializeContextInputs(saga.WorkflowInputsJson),
            StringComparer.Ordinal);

        workflowBag.TryGetValue(RejectionHistoryAccumulator.WorkflowVariableKey, out var existingElement);
        var existingValue = existingElement.ValueKind == JsonValueKind.String
            ? existingElement.GetString()
            : existingElement.ValueKind == JsonValueKind.Undefined || existingElement.ValueKind == JsonValueKind.Null
                ? null
                : existingElement.GetRawText();

        var justFinishedRound = message.ReviewRound ?? 1;
        var updated = RejectionHistoryAccumulator.Append(existingValue, justFinishedRound, artifactBody, config);

        workflowBag[RejectionHistoryAccumulator.WorkflowVariableKey] =
            JsonSerializer.SerializeToElement(updated);

        saga.WorkflowInputsJson = SerializeContextInputs(workflowBag);
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
            WorkflowNodeKind.Swarm =>
                PublishSwarmEntryAsync(context, agentConfigRepo, artifactStore, saga, workflow, node, inputRef, roundId),
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
            LoopDecision: loopDecision,
            Repositories: ParseRepositoriesJson(saga.RepositoriesJson),
            TraceWorkDir: saga.TraceWorkDir));
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
            ReviewMaxRounds: saga.ParentReviewMaxRounds,
            OptOutLastRoundReminder: targetNode.OptOutLastRoundReminder,
            Repositories: ParseRepositoriesJson(saga.RepositoriesJson),
            TraceWorkDir: saga.TraceWorkDir));
    }

    private static CodeFlow.Contracts.RetryContext? BuildRetryContextForHandoff(
        IRetryContextBuilder retryContextBuilder,
        WorkflowSagaStateEntity saga,
        AgentInvocationCompleted message)
    {
        if (!string.Equals(message.OutputPortName, "Failed", StringComparison.Ordinal))
        {
            return null;
        }

        var attemptNumber = CountPriorFailedAttempts(saga) + 1;
        var snapshot = retryContextBuilder.Build(attemptNumber, message.DecisionPayload);
        return RetryContextBuilder.ToContract(snapshot);
    }

    private static int CountPriorFailedAttempts(WorkflowSagaStateEntity saga)
    {
        var history = saga.GetDecisionHistory();
        return history.Count(record =>
            record.RoundId == saga.CurrentRoundId
            && string.Equals(record.Decision, "Failed", StringComparison.Ordinal));
    }

    private static (string? Reason, string? Summary) ExtractFailureContext(JsonElement? payload) =>
        RetryContextBuilder.ExtractFailureContext(payload);

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

    /// <summary>
    /// Per-trace repository allowlist key on the workflow-context bag. When this key appears
    /// in <c>workflow.*</c> at saga init or via a <c>setWorkflow</c> mutation, the value (a JSON
    /// array of <c>{url, branch?}</c>) is lifted to <c>saga.RepositoriesJson</c> so it survives
    /// subflow boundaries. sc-607: this used to read from <c>context.*</c>, but per-trace state
    /// belongs on the trace-tree-shared bag, parallel to <c>workflow.workDir</c>.
    /// </summary>
    private const string RepositoriesWorkflowKey = "repositories";

    /// <summary>
    /// If the workflow-context bag contains a <c>repositories</c> array, return its canonical
    /// JSON form for storage on <c>saga.RepositoriesJson</c>. Null when absent or when the value
    /// is not a JSON array. Validates only structure — individual entries are filtered at publish
    /// time by <see cref="ParseRepositoriesJson"/>.
    /// </summary>
    private static string? LiftRepositoriesFromWorkflowBag(
        IReadOnlyDictionary<string, JsonElement>? workflowContext)
    {
        if (workflowContext is null
            || !workflowContext.TryGetValue(RepositoriesWorkflowKey, out var repos)
            || repos.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return repos.GetRawText();
    }

    /// <summary>
    /// Parse <c>saga.RepositoriesJson</c> into the contract shape published on
    /// <see cref="AgentInvokeRequested.Repositories"/> / <see cref="SubflowInvokeRequested.Repositories"/>.
    /// Returns null when the saga has no allowlist; returns an empty list only if the JSON parses
    /// but contains no valid <c>{url}</c> entries (malformed entries are silently skipped).
    /// </summary>
    private static IReadOnlyList<RepositoryDeclaration>? ParseRepositoriesJson(string? repositoriesJson)
    {
        if (string.IsNullOrWhiteSpace(repositoriesJson))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(repositoriesJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<RepositoryDeclaration>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("url", out var urlElement)
                    || urlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var url = urlElement.GetString();
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                string? branch = null;
                if (entry.TryGetProperty("branch", out var branchElement)
                    && branchElement.ValueKind == JsonValueKind.String)
                {
                    var b = branchElement.GetString();
                    if (!string.IsNullOrWhiteSpace(b))
                    {
                        branch = b;
                    }
                }

                result.Add(new RepositoryDeclaration(url, branch));
            }

            return result;
        }
    }

    /// <summary>
    /// Serialize a parent saga's published <see cref="SubflowInvokeRequested.Repositories"/>
    /// snapshot back into the canonical JSON form for storage on the child saga's
    /// <c>RepositoriesJson</c>. Inverse of <see cref="ParseRepositoriesJson"/>.
    /// </summary>
    private static string? SerializeRepositories(IReadOnlyList<RepositoryDeclaration>? repositories)
    {
        if (repositories is null || repositories.Count == 0)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var repo in repositories)
            {
                writer.WriteStartObject();
                writer.WriteString("url", repo.Url);
                if (!string.IsNullOrWhiteSpace(repo.Branch))
                {
                    writer.WriteString("branch", repo.Branch);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
