using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using CodeFlow.Runtime;

namespace CodeFlow.Orchestration.DryRun;

/// <summary>
/// In-process workflow walker that simulates a real run without invoking the LLM, the bus, or
/// the saga checkpointing layer. Designed for the T1 dry-run UX — the author clicks "run" and
/// gets a sub-second deterministic trace of which nodes would fire and with what artifacts.
///
/// Scope notes (v4):
/// <list type="bullet">
///   <item><description>Supports node kinds Start, Agent, Logic, Hitl, Subflow, ReviewLoop, Transform.</description></item>
///   <item><description><b>Swarm nodes are non-replayable</b> (sc-43). Encountering one fails the
///     walk with a clear message — replay-with-edit can't substitute prior contributor /
///     synthesizer outputs the way it does for Agent/Hitl, and the dry-run executor doesn't
///     invoke live LLMs to re-run the swarm fresh. Authors who need to vary upstream input to a
///     swarm should branch the workflow at the swarm's input port.</description></item>
///   <item><description>Reuses <see cref="LogicNodeScriptHost"/> for Logic-node script execution
///     AND for author-attached input scripts (Start/Agent/Hitl) and output scripts (Agent), so
///     script semantics match the saga byte-for-byte.</description></item>
///   <item><description>Honors the built-ins: P3 (rejection-history accumulation in ReviewLoop
///     bodies), P4 (mirror output to workflow var), and P5 (output-port replacements).</description></item>
///   <item><description>v3: when <see cref="IAgentConfigRepository"/> +
///     <see cref="IScribanTemplateRenderer"/> are wired, applies decision-output templates to
///     Agent submissions (skipped when an output script set <c>setOutput</c>) and surfaces HITL
///     form-template metadata + a best-effort rendered preview on
///     <see cref="DryRunHitlPayload"/>.</description></item>
///   <item><description>v4 (this iteration): emits a <see cref="DryRunEventKind.RetryContextHandoff"/>
///     event when an Agent's effective port resolves to <c>Failed</c> and routes to a wired edge —
///     mirrors the saga's <c>BuildRetryContextForHandoff</c> +
///     <c>CountPriorFailedAttempts</c> per-walk semantics so authors can see the saga would
///     synthesize <c>{ attemptNumber, priorFailureReason, priorAttemptSummary }</c> for the next
///     invocation. The dry-run never invokes a model so the retry note isn't injected anywhere,
///     but the diagnostic event captures the parity surface.</description></item>
///   <item><description>Does NOT render full agent prompt templates; the trace records the input
///     artifact a node receives, which is what authors typically want to inspect anyway.</description></item>
/// </list>
/// </summary>
public sealed class DryRunExecutor
{
    public const int MaxStepsPerRun = 256;

    private readonly IWorkflowRepository workflowRepository;
    private readonly LogicNodeScriptHost logicScriptHost;
    private readonly IAgentConfigRepository? agentConfigRepository;
    private readonly IScribanTemplateRenderer? templateRenderer;
    private readonly IDecisionTemplateRenderer? decisionTemplateRenderer;
    private readonly IRetryContextBuilder retryContextBuilder;

    public DryRunExecutor(
        IWorkflowRepository workflowRepository,
        LogicNodeScriptHost logicScriptHost,
        IAgentConfigRepository? agentConfigRepository = null,
        IScribanTemplateRenderer? templateRenderer = null,
        IDecisionTemplateRenderer? decisionTemplateRenderer = null,
        IRetryContextBuilder? retryContextBuilder = null)
    {
        this.workflowRepository = workflowRepository ?? throw new ArgumentNullException(nameof(workflowRepository));
        this.logicScriptHost = logicScriptHost ?? throw new ArgumentNullException(nameof(logicScriptHost));
        // The agent-config + template renderer pair is optional. Both must be supplied together
        // for v3 features to engage; passing only one is a programmer error since neither is
        // useful without the other.
        if ((agentConfigRepository is null) ^ (templateRenderer is null))
        {
            throw new ArgumentException(
                $"{nameof(agentConfigRepository)} and {nameof(templateRenderer)} must be supplied together; "
                + "passing only one disables decision-output templates and HITL form rendering.");
        }
        this.agentConfigRepository = agentConfigRepository;
        this.templateRenderer = templateRenderer;
        // Construct a default decision-template renderer over the supplied scriban renderer when
        // the caller didn't pass one — keeps test wiring simple and matches saga DI behavior.
        this.decisionTemplateRenderer = decisionTemplateRenderer
            ?? (templateRenderer is null ? null : new DecisionTemplateRenderer(templateRenderer));
        // Retry-context extraction is pure (no IO, no config) so a default instance keeps test
        // wiring simple. Saga + dry-run share `RetryContextBuilder` so silent drift between the
        // two engines on "what counts as a prior failure reason" is no longer possible.
        this.retryContextBuilder = retryContextBuilder ?? new RetryContextBuilder();
    }

    public async Task<DryRunResult> ExecuteAsync(
        DryRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowKey);

        var rootWorkflow = request.WorkflowVersion is int v
            ? await workflowRepository.GetAsync(request.WorkflowKey, v, cancellationToken)
            : await workflowRepository.GetLatestAsync(request.WorkflowKey, cancellationToken);

        if (rootWorkflow is null)
        {
            return DryRunResult.Failure(
                $"Workflow '{request.WorkflowKey}' v{request.WorkflowVersion?.ToString() ?? "latest"} not found.");
        }

        var state = new DryRunState(request.MockResponses);
        var subState = await WalkWorkflowAsync(
            workflow: rootWorkflow,
            startingInput: request.StartingInput,
            initialContext: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            initialWorkflow: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            depth: 0,
            reviewRound: null,
            maxRounds: null,
            state: state,
            cancellationToken: cancellationToken);

        // Roll the leaf walk's result up into the executor result. For the top-level workflow
        // there is no parent loop to absorb the terminal port, so the leaf state IS the result.
        return new DryRunResult(
            State: subState.Terminal,
            TerminalPort: subState.TerminalPort,
            FailureReason: subState.FailureReason,
            FinalArtifact: subState.FinalArtifact,
            HitlPayload: subState.HitlPayload,
            WorkflowVariables: subState.WorkflowVars,
            ContextVariables: subState.ContextVars,
            Events: state.Events);
    }

    /// <summary>
    /// Walk a single workflow (the top-level or a subflow body). Returns the walk's terminal
    /// state. Caller's responsibility to map ReviewLoop iteration semantics — this method is
    /// "single pass through the workflow" and not aware of looping.
    /// </summary>
    private async Task<DryRunWalkResult> WalkWorkflowAsync(
        Workflow workflow,
        string? startingInput,
        IReadOnlyDictionary<string, JsonElement> initialContext,
        IReadOnlyDictionary<string, JsonElement> initialWorkflow,
        int depth,
        int? reviewRound,
        int? maxRounds,
        DryRunState state,
        CancellationToken cancellationToken)
    {
        var contextVars = new Dictionary<string, JsonElement>(initialContext, StringComparer.Ordinal);
        var workflowVars = new Dictionary<string, JsonElement>(initialWorkflow, StringComparer.Ordinal);

        WorkflowNode currentNode;
        try
        {
            currentNode = workflow.StartNode;
        }
        catch (InvalidOperationException)
        {
            return DryRunWalkResult.Failed(
                $"Workflow '{workflow.Key}' v{workflow.Version} has no Start node.",
                contextVars,
                workflowVars);
        }

        var currentInput = startingInput;
        string? lastEffectivePort = null;
        // Saga parity: per-round Failed-decision count drives the retry-context attemptNumber.
        // Each WalkWorkflowAsync invocation models one saga round (top-level walk, single subflow
        // body, or one ReviewLoop iteration), so the counter resets on entry exactly the way the
        // saga's CurrentRoundId rotation resets CountPriorFailedAttempts.
        var failedAttemptCount = 0;

        for (var step = 0; step < MaxStepsPerRun; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.RecordEvent(new DryRunEvent(
                Ordinal: state.NextOrdinal(),
                Kind: DryRunEventKind.NodeEntered,
                NodeId: currentNode.Id,
                NodeKind: currentNode.Kind.ToString(),
                AgentKey: currentNode.AgentKey,
                PortName: null,
                Message: null,
                InputPreview: Preview(currentInput),
                OutputPreview: null,
                ReviewRound: reviewRound,
                MaxRounds: maxRounds,
                SubflowDepth: depth,
                SubflowKey: null,
                SubflowVersion: null,
                Logs: null,
                DecisionPayload: null));

            switch (currentNode.Kind)
            {
                case WorkflowNodeKind.Start:
                {
                    var startInputOutcome = RunInputScriptIfPresent(
                        workflow, currentNode, currentInput, contextVars, workflowVars,
                        reviewRound, maxRounds, depth, state, cancellationToken);
                    if (startInputOutcome.FailureReason is not null)
                    {
                        return DryRunWalkResult.Failed(startInputOutcome.FailureReason, contextVars, workflowVars);
                    }
                    currentInput = startInputOutcome.EffectiveInput;

                    // Start nodes are pass-through: route on their first declared output port,
                    // or "Completed" by default if the author left it blank.
                    var startPort = currentNode.OutputPorts.FirstOrDefault() ?? "Completed";
                    lastEffectivePort = startPort;
                    var nextEdge = workflow.FindNext(currentNode.Id, startPort);
                    if (nextEdge is null)
                    {
                        return CompleteWorkflow(workflow, currentNode, startPort, currentInput, contextVars, workflowVars, state);
                    }
                    state.RecordEvent(EdgeEvent(state, currentNode, startPort, nextEdge, depth));
                    currentNode = workflow.FindNode(nextEdge.ToNodeId)
                        ?? throw new InvalidOperationException(
                            $"Edge from {currentNode.Id} on '{startPort}' targets unknown node {nextEdge.ToNodeId}.");
                    break;
                }

                case WorkflowNodeKind.Agent:
                {
                    if (string.IsNullOrWhiteSpace(currentNode.AgentKey))
                    {
                        return DryRunWalkResult.Failed(
                            $"Agent node {currentNode.Id} has no AgentKey.",
                            contextVars, workflowVars);
                    }

                    var agentInputOutcome = RunInputScriptIfPresent(
                        workflow, currentNode, currentInput, contextVars, workflowVars,
                        reviewRound, maxRounds, depth, state, cancellationToken);
                    if (agentInputOutcome.FailureReason is not null)
                    {
                        return DryRunWalkResult.Failed(agentInputOutcome.FailureReason, contextVars, workflowVars);
                    }
                    currentInput = agentInputOutcome.EffectiveInput;

                    if (!state.TryDequeueMock(currentNode.AgentKey!, out var mock))
                    {
                        return DryRunWalkResult.Failed(
                            $"No mock response queued for agent '{currentNode.AgentKey}' (workflow '{workflow.Key}' v{workflow.Version}).",
                            contextVars, workflowVars);
                    }

                    var output = mock!.Output ?? string.Empty;

                    // P4: mirror agent output text into the named workflow variable BEFORE
                    // routing, so downstream Logic nodes / port replacements observe it.
                    var mirrorTarget = AgentOutputTransforms.NormalizeMirrorTarget(currentNode.MirrorOutputToWorkflowVar);
                    if (mirrorTarget is not null)
                    {
                        var mirrored = AgentOutputTransforms.Mirror(workflowVars, mirrorTarget, output);
                        // Replace the mutable bag's contents so downstream steps see the update.
                        workflowVars.Clear();
                        foreach (var (k, v) in mirrored)
                        {
                            workflowVars[k] = v;
                        }
                        state.RecordEvent(new DryRunEvent(
                            Ordinal: state.NextOrdinal(),
                            Kind: DryRunEventKind.BuiltinApplied,
                            NodeId: currentNode.Id,
                            NodeKind: currentNode.Kind.ToString(),
                            AgentKey: currentNode.AgentKey,
                            PortName: null,
                            Message: $"P4 mirror: workflow.{mirrorTarget} ← agent output ({output.Length} chars).",
                            InputPreview: null,
                            OutputPreview: null,
                            ReviewRound: reviewRound,
                            MaxRounds: maxRounds,
                            SubflowDepth: depth,
                            SubflowKey: null,
                            SubflowVersion: null,
                            Logs: null,
                            DecisionPayload: null));
                    }

                    state.RecordEvent(new DryRunEvent(
                        Ordinal: state.NextOrdinal(),
                        Kind: DryRunEventKind.AgentMockApplied,
                        NodeId: currentNode.Id,
                        NodeKind: currentNode.Kind.ToString(),
                        AgentKey: currentNode.AgentKey,
                        PortName: mock.Decision,
                        Message: null,
                        InputPreview: Preview(currentInput),
                        OutputPreview: Preview(output),
                        ReviewRound: reviewRound,
                        MaxRounds: maxRounds,
                        SubflowDepth: depth,
                        SubflowKey: null,
                        SubflowVersion: null,
                        Logs: null,
                        DecisionPayload: mock.Payload?.DeepClone()));

                    var effectivePort = mock.Decision;
                    lastEffectivePort = effectivePort;
                    var effectiveOutput = output;
                    var outputWasScriptOverridden = false;

                    // Author-attached output script (Agent only): mirrors saga semantics — the
                    // script sees the agent's `output` (text + decision metadata) and may call
                    // setWorkflow / setContext / setNodePath / setOutput. setNodePath wins over
                    // mock.Decision; setOutput wins over the artifact when set.
                    if (!string.IsNullOrWhiteSpace(currentNode.OutputScript))
                    {
                        var outputScriptOutcome = RunAgentOutputScript(
                            workflow, currentNode, output, mock.Decision, mock.Payload,
                            contextVars, workflowVars, reviewRound, maxRounds, depth, state, cancellationToken);
                        if (outputScriptOutcome.FailureReason is not null)
                        {
                            return DryRunWalkResult.Failed(outputScriptOutcome.FailureReason, contextVars, workflowVars);
                        }
                        effectivePort = outputScriptOutcome.Port;
                        effectiveOutput = outputScriptOutcome.Output;
                        outputWasScriptOverridden = outputScriptOutcome.SetOutputCalled;
                        lastEffectivePort = effectivePort;
                    }

                    // v3: decision-output templates render server-side after the output script
                    // resolves the port. Skipped when the script issued setOutput() so authors
                    // retain the documented escape hatch. Mirrors saga TryApplyDecisionOutputTemplateAsync.
                    if (!outputWasScriptOverridden
                        && agentConfigRepository is not null
                        && templateRenderer is not null)
                    {
                        var templateOutcome = await TryApplyDecisionOutputTemplateAsync(
                            currentNode, mock.Decision, effectivePort, effectiveOutput,
                            currentInput, contextVars, workflowVars,
                            reviewRound, maxRounds, depth, state, cancellationToken);
                        if (templateOutcome.FailureReason is not null)
                        {
                            return DryRunWalkResult.Failed(templateOutcome.FailureReason, contextVars, workflowVars);
                        }
                        if (templateOutcome.OverrideOutput is { } overrideText)
                        {
                            effectiveOutput = overrideText;
                        }
                    }

                    // P5: if the port has a configured replacement, swap the artifact for the
                    // named workflow variable's contents. Runs AFTER the output script so the
                    // script-managed variable updates are visible.
                    var portReplacements = AgentOutputTransforms.NormalizePortReplacements(currentNode.OutputPortReplacements);
                    if (portReplacements is not null)
                    {
                        var (replacementText, boundVar) = AgentOutputTransforms.ResolvePortReplacement(
                            portReplacements, effectivePort, workflowVars);
                        if (replacementText is not null)
                        {
                            effectiveOutput = replacementText;
                            state.RecordEvent(new DryRunEvent(
                                Ordinal: state.NextOrdinal(),
                                Kind: DryRunEventKind.BuiltinApplied,
                                NodeId: currentNode.Id,
                                NodeKind: currentNode.Kind.ToString(),
                                AgentKey: currentNode.AgentKey,
                                PortName: effectivePort,
                                Message: $"P5 replace: artifact on '{effectivePort}' ← workflow.{boundVar} ({effectiveOutput.Length} chars).",
                                InputPreview: null,
                                OutputPreview: Preview(effectiveOutput),
                                ReviewRound: reviewRound,
                                MaxRounds: maxRounds,
                                SubflowDepth: depth,
                                SubflowKey: null,
                                SubflowVersion: null,
                                Logs: null,
                                DecisionPayload: null));
                        }
                    }

                    var agentEdge = workflow.FindNext(currentNode.Id, effectivePort);
                    if (agentEdge is null)
                    {
                        if (string.Equals(effectivePort, "Failed", StringComparison.Ordinal))
                        {
                            return DryRunWalkResult.Failed(
                                $"Node {currentNode.Id} failed and no '{effectivePort}' edge is wired.",
                                contextVars, workflowVars);
                        }
                        return CompleteWorkflow(workflow, currentNode, effectivePort, effectiveOutput, contextVars, workflowVars, state);
                    }
                    state.RecordEvent(EdgeEvent(state, currentNode, effectivePort, agentEdge, depth));

                    // Saga-parity retry-context handoff: a Failed effective port routing to any
                    // wired edge means the saga's BuildRetryContextForHandoff would synthesize a
                    // RetryContext for the next invocation. Increment the counter to mirror the
                    // saga's CountPriorFailedAttempts (history-based count + 1 for the new event).
                    if (string.Equals(effectivePort, "Failed", StringComparison.Ordinal))
                    {
                        failedAttemptCount++;
                        var retryTargetNode = workflow.FindNode(agentEdge.ToNodeId);
                        var snapshot = retryContextBuilder.Build(
                            attemptNumber: failedAttemptCount + 1,
                            decisionPayload: RetryContextBuilder.AsJsonElement(mock.Payload));
                        state.RecordEvent(new DryRunEvent(
                            Ordinal: state.NextOrdinal(),
                            Kind: DryRunEventKind.RetryContextHandoff,
                            NodeId: agentEdge.ToNodeId,
                            NodeKind: retryTargetNode?.Kind.ToString() ?? "Unknown",
                            AgentKey: retryTargetNode?.AgentKey,
                            PortName: effectivePort,
                            Message: RetryContextBuilder.ToMessage(snapshot),
                            InputPreview: null,
                            OutputPreview: null,
                            ReviewRound: reviewRound,
                            MaxRounds: maxRounds,
                            SubflowDepth: depth,
                            SubflowKey: null,
                            SubflowVersion: null,
                            Logs: null,
                            DecisionPayload: RetryContextBuilder.ToJsonNode(snapshot)));
                    }

                    currentNode = workflow.FindNode(agentEdge.ToNodeId)
                        ?? throw new InvalidOperationException(
                            $"Edge from {currentNode.Id} on '{effectivePort}' targets unknown node {agentEdge.ToNodeId}.");
                    currentInput = effectiveOutput;
                    break;
                }

                case WorkflowNodeKind.Logic:
                {
                    var script = currentNode.OutputScript ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(script))
                    {
                        return DryRunWalkResult.Failed(
                            $"Logic node {currentNode.Id} has no script.",
                            contextVars, workflowVars);
                    }

                    var inputElement = ParseInputAsJson(currentInput);
                    var eval = logicScriptHost.Evaluate(
                        workflowKey: workflow.Key,
                        workflowVersion: workflow.Version,
                        nodeId: currentNode.Id,
                        script: script,
                        declaredPorts: currentNode.OutputPorts,
                        input: inputElement,
                        context: contextVars,
                        cancellationToken: cancellationToken,
                        workflow: workflowVars,
                        reviewRound: reviewRound,
                        reviewMaxRounds: maxRounds,
                        allowOutputOverride: false,
                        allowInputOverride: false);

                    state.RecordEvent(new DryRunEvent(
                        Ordinal: state.NextOrdinal(),
                        Kind: DryRunEventKind.LogicEvaluated,
                        NodeId: currentNode.Id,
                        NodeKind: currentNode.Kind.ToString(),
                        AgentKey: null,
                        PortName: eval.OutputPortName,
                        Message: eval.Failure is null ? null : $"{eval.Failure}: {eval.FailureMessage}",
                        InputPreview: Preview(currentInput),
                        OutputPreview: null,
                        ReviewRound: reviewRound,
                        MaxRounds: maxRounds,
                        SubflowDepth: depth,
                        SubflowKey: null,
                        SubflowVersion: null,
                        Logs: eval.LogEntries,
                        DecisionPayload: null));

                    if (eval.Failure is not null)
                    {
                        return DryRunWalkResult.Failed(
                            $"Logic node {currentNode.Id} failed ({eval.Failure}): {eval.FailureMessage}",
                            contextVars, workflowVars);
                    }

                    foreach (var (k, v) in eval.ContextUpdates)
                    {
                        contextVars[k] = v;
                    }
                    foreach (var (k, v) in eval.WorkflowUpdates)
                    {
                        workflowVars[k] = v;
                    }

                    var logicPort = eval.OutputPortName!;
                    lastEffectivePort = logicPort;
                    var logicEdge = workflow.FindNext(currentNode.Id, logicPort);
                    if (logicEdge is null)
                    {
                        return CompleteWorkflow(workflow, currentNode, logicPort, currentInput, contextVars, workflowVars, state);
                    }
                    state.RecordEvent(EdgeEvent(state, currentNode, logicPort, logicEdge, depth));
                    currentNode = workflow.FindNode(logicEdge.ToNodeId)
                        ?? throw new InvalidOperationException(
                            $"Edge from {currentNode.Id} on '{logicPort}' targets unknown node {logicEdge.ToNodeId}.");
                    break;
                }

                case WorkflowNodeKind.Hitl:
                {
                    var hitlInputOutcome = RunInputScriptIfPresent(
                        workflow, currentNode, currentInput, contextVars, workflowVars,
                        reviewRound, maxRounds, depth, state, cancellationToken);
                    if (hitlInputOutcome.FailureReason is not null)
                    {
                        return DryRunWalkResult.Failed(hitlInputOutcome.FailureReason, contextVars, workflowVars);
                    }
                    currentInput = hitlInputOutcome.EffectiveInput;

                    // v3: when wired, look up the HITL agent's config so the suspension payload can
                    // surface the legacy `outputTemplate` (form preview rendered client-side in
                    // hitl-review.component.ts) and structured `decisionOutputTemplates` (saga's
                    // submit-time render). Plus a best-effort server render so authors can spot
                    // template typos without launching a real run.
                    var hitlPayload = await BuildHitlPayloadAsync(
                        currentNode, currentInput, contextVars, workflowVars, cancellationToken);

                    state.RecordEvent(new DryRunEvent(
                        Ordinal: state.NextOrdinal(),
                        Kind: DryRunEventKind.HitlSuspended,
                        NodeId: currentNode.Id,
                        NodeKind: currentNode.Kind.ToString(),
                        AgentKey: currentNode.AgentKey,
                        PortName: null,
                        Message: hitlPayload.RenderError is null
                            ? "Dry-run halted at HITL node; form would be presented to a human reviewer."
                            : $"Dry-run halted at HITL node; form-template render failed: {hitlPayload.RenderError}",
                        InputPreview: Preview(currentInput),
                        OutputPreview: Preview(hitlPayload.RenderedFormPreview),
                        ReviewRound: reviewRound,
                        MaxRounds: maxRounds,
                        SubflowDepth: depth,
                        SubflowKey: null,
                        SubflowVersion: null,
                        Logs: null,
                        DecisionPayload: null));

                    return new DryRunWalkResult(
                        Terminal: DryRunTerminalState.HitlReached,
                        TerminalPort: null,
                        FailureReason: null,
                        FinalArtifact: currentInput,
                        HitlPayload: hitlPayload,
                        ContextVars: contextVars,
                        WorkflowVars: workflowVars);
                }

                case WorkflowNodeKind.Subflow:
                case WorkflowNodeKind.ReviewLoop:
                {
                    if (string.IsNullOrWhiteSpace(currentNode.SubflowKey)
                        || currentNode.SubflowVersion is not int subVersion)
                    {
                        return DryRunWalkResult.Failed(
                            $"Subflow node {currentNode.Id} ({currentNode.Kind}) has no pinned subflow.",
                            contextVars, workflowVars);
                    }

                    Workflow subflow;
                    try
                    {
                        subflow = await workflowRepository.GetAsync(
                            currentNode.SubflowKey!,
                            subVersion,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        return DryRunWalkResult.Failed(
                            $"Failed to load subflow '{currentNode.SubflowKey}' v{subVersion}: {ex.Message}",
                            contextVars, workflowVars);
                    }

                    // Boundary input script: runs once before the child walk. For ReviewLoop,
                    // ExecuteReviewLoopAsync re-uses the rewritten input across all iterations —
                    // the script never fires per-iteration. Mirrors the saga's
                    // PublishSubflowDispatchAsync(runInputScript: true) behavior.
                    var subInputOutcome = RunInputScriptIfPresent(
                        workflow, currentNode, currentInput, contextVars, workflowVars,
                        reviewRound, maxRounds, depth, state, cancellationToken);
                    if (subInputOutcome.FailureReason is not null)
                    {
                        return DryRunWalkResult.Failed(subInputOutcome.FailureReason, contextVars, workflowVars);
                    }
                    currentInput = subInputOutcome.EffectiveInput;

                    state.RecordEvent(new DryRunEvent(
                        Ordinal: state.NextOrdinal(),
                        Kind: DryRunEventKind.SubflowEntered,
                        NodeId: currentNode.Id,
                        NodeKind: currentNode.Kind.ToString(),
                        AgentKey: null,
                        PortName: null,
                        Message: null,
                        InputPreview: Preview(currentInput),
                        OutputPreview: null,
                        ReviewRound: null,
                        MaxRounds: null,
                        SubflowDepth: depth + 1,
                        SubflowKey: currentNode.SubflowKey,
                        SubflowVersion: subVersion,
                        Logs: null,
                        DecisionPayload: null));

                    DryRunWalkResult subResult;
                    if (currentNode.Kind == WorkflowNodeKind.ReviewLoop)
                    {
                        subResult = await ExecuteReviewLoopAsync(
                            currentNode,
                            subflow,
                            currentInput,
                            contextVars,
                            workflowVars,
                            depth + 1,
                            state,
                            cancellationToken);
                    }
                    else
                    {
                        subResult = await WalkWorkflowAsync(
                            subflow,
                            currentInput,
                            contextVars,
                            workflowVars,
                            depth + 1,
                            reviewRound: null,
                            maxRounds: null,
                            state,
                            cancellationToken);
                    }

                    state.RecordEvent(new DryRunEvent(
                        Ordinal: state.NextOrdinal(),
                        Kind: DryRunEventKind.SubflowExited,
                        NodeId: currentNode.Id,
                        NodeKind: currentNode.Kind.ToString(),
                        AgentKey: null,
                        PortName: subResult.TerminalPort,
                        Message: subResult.FailureReason,
                        InputPreview: null,
                        OutputPreview: Preview(subResult.FinalArtifact),
                        ReviewRound: null,
                        MaxRounds: null,
                        SubflowDepth: depth,
                        SubflowKey: currentNode.SubflowKey,
                        SubflowVersion: subVersion,
                        Logs: null,
                        DecisionPayload: null));

                    // Propagate child workflow-vars and context-vars (last-write-wins) so the
                    // parent walk sees subflow-side updates.
                    foreach (var (k, v) in subResult.WorkflowVars)
                    {
                        workflowVars[k] = v;
                    }
                    foreach (var (k, v) in subResult.ContextVars)
                    {
                        contextVars[k] = v;
                    }

                    // HITL inside a subflow stops the whole run — the parent can't continue
                    // until a human decides.
                    if (subResult.Terminal == DryRunTerminalState.HitlReached)
                    {
                        return subResult with
                        {
                            ContextVars = contextVars,
                            WorkflowVars = workflowVars,
                        };
                    }

                    if (subResult.Terminal == DryRunTerminalState.Failed
                        || subResult.Terminal == DryRunTerminalState.StepLimitExceeded)
                    {
                        return subResult with
                        {
                            ContextVars = contextVars,
                            WorkflowVars = workflowVars,
                        };
                    }

                    var subTerminalPort = subResult.TerminalPort ?? "Completed";
                    var subEffectiveOutput = subResult.FinalArtifact;

                    // Boundary output script: runs once after the child terminates with the
                    // effective port resolved (for ReviewLoop, after the loop has fully exited).
                    // May rewrite the routing port (setNodePath), the artifact (setOutput), and
                    // context/workflow vars. Mirrors the saga's TryEvaluateBoundaryOutputScriptAsync
                    // hook in RouteSubflowCompletionAsync.
                    if (!string.IsNullOrWhiteSpace(currentNode.OutputScript))
                    {
                        var boundaryOutcome = RunBoundaryOutputScript(
                            workflow, currentNode, subEffectiveOutput ?? string.Empty, subTerminalPort,
                            contextVars, workflowVars, reviewRound, maxRounds, depth, state, cancellationToken);
                        if (boundaryOutcome.FailureReason is not null)
                        {
                            return DryRunWalkResult.Failed(boundaryOutcome.FailureReason, contextVars, workflowVars);
                        }
                        subTerminalPort = boundaryOutcome.ResolvedPort;
                        subEffectiveOutput = boundaryOutcome.ResolvedOutput;
                    }

                    lastEffectivePort = subTerminalPort;

                    var subEdge = workflow.FindNext(currentNode.Id, subTerminalPort);
                    if (subEdge is null)
                    {
                        if (string.Equals(subTerminalPort, "Failed", StringComparison.Ordinal))
                        {
                            return DryRunWalkResult.Failed(
                                $"Subflow node {currentNode.Id} terminated on '{subTerminalPort}' with no wired edge.",
                                contextVars, workflowVars);
                        }
                        return CompleteWorkflow(workflow, currentNode, subTerminalPort, subEffectiveOutput, contextVars, workflowVars, state);
                    }
                    state.RecordEvent(EdgeEvent(state, currentNode, subTerminalPort, subEdge, depth));
                    currentNode = workflow.FindNode(subEdge.ToNodeId)
                        ?? throw new InvalidOperationException(
                            $"Edge from subflow {currentNode.Id} on '{subTerminalPort}' targets unknown node {subEdge.ToNodeId}.");
                    currentInput = subEffectiveOutput;
                    break;
                }

                case WorkflowNodeKind.Transform:
                {
                    if (string.IsNullOrWhiteSpace(currentNode.Template))
                    {
                        return DryRunWalkResult.Failed(
                            $"Transform node {currentNode.Id} has no template.",
                            contextVars, workflowVars);
                    }
                    if (templateRenderer is null)
                    {
                        return DryRunWalkResult.Failed(
                            $"Transform node {currentNode.Id} cannot render: dry-run was not configured with an IScribanTemplateRenderer.",
                            contextVars, workflowVars);
                    }

                    // 1. inputScript shapes the structured input the template will see. Reuses the
                    //    same helper Start/Agent/Hitl nodes use, so semantics match byte-for-byte.
                    var transformInputOutcome = RunInputScriptIfPresent(
                        workflow, currentNode, currentInput, contextVars, workflowVars,
                        reviewRound, maxRounds, depth, state, cancellationToken);
                    if (transformInputOutcome.FailureReason is not null)
                    {
                        return DryRunWalkResult.Failed(transformInputOutcome.FailureReason, contextVars, workflowVars);
                    }
                    currentInput = transformInputOutcome.EffectiveInput;

                    var inputElement = ParseInputAsJson(currentInput);
                    var scope = TransformNodeContext.Build(inputElement, contextVars, workflowVars);

                    string rendered;
                    try
                    {
                        rendered = templateRenderer.Render(currentNode.Template!, scope, cancellationToken);
                    }
                    catch (PromptTemplateException ex)
                    {
                        var failedEdge = workflow.FindNext(currentNode.Id, "Failed");
                        var transformFailMessage = $"Transform node {currentNode.Id} render failed: {ex.Message}";
                        state.RecordEvent(new DryRunEvent(
                            Ordinal: state.NextOrdinal(),
                            Kind: DryRunEventKind.TransformRendered,
                            NodeId: currentNode.Id,
                            NodeKind: currentNode.Kind.ToString(),
                            AgentKey: null,
                            PortName: "Failed",
                            Message: transformFailMessage,
                            InputPreview: Preview(currentInput),
                            OutputPreview: null,
                            ReviewRound: reviewRound,
                            MaxRounds: maxRounds,
                            SubflowDepth: depth,
                            SubflowKey: null,
                            SubflowVersion: null,
                            Logs: null,
                            DecisionPayload: null));

                        if (failedEdge is null)
                        {
                            return DryRunWalkResult.Failed(transformFailMessage, contextVars, workflowVars);
                        }

                        lastEffectivePort = "Failed";
                        state.RecordEvent(EdgeEvent(state, currentNode, "Failed", failedEdge, depth));
                        currentNode = workflow.FindNode(failedEdge.ToNodeId)
                            ?? throw new InvalidOperationException(
                                $"Edge from transform {currentNode.Id} on 'Failed' targets unknown node {failedEdge.ToNodeId}.");
                        // Pass the failure message through as the input artifact so downstream
                        // diagnostics see the same context the saga's Failed-route would carry.
                        currentInput = transformFailMessage;
                        break;
                    }

                    var isJsonMode = string.Equals(currentNode.OutputType, "json", StringComparison.Ordinal);
                    if (isJsonMode)
                    {
                        try
                        {
                            using var _ = JsonDocument.Parse(rendered);
                        }
                        catch (JsonException ex)
                        {
                            var failedEdge = workflow.FindNext(currentNode.Id, "Failed");
                            var jsonFailMessage = $"Transform node {currentNode.Id} produced invalid JSON (outputType=json): {ex.Message}";
                            state.RecordEvent(new DryRunEvent(
                                Ordinal: state.NextOrdinal(),
                                Kind: DryRunEventKind.TransformRendered,
                                NodeId: currentNode.Id,
                                NodeKind: currentNode.Kind.ToString(),
                                AgentKey: null,
                                PortName: "Failed",
                                Message: jsonFailMessage,
                                InputPreview: Preview(currentInput),
                                OutputPreview: Preview(rendered),
                                ReviewRound: reviewRound,
                                MaxRounds: maxRounds,
                                SubflowDepth: depth,
                                SubflowKey: null,
                                SubflowVersion: null,
                                Logs: null,
                                DecisionPayload: null));

                            if (failedEdge is null)
                            {
                                return DryRunWalkResult.Failed(jsonFailMessage, contextVars, workflowVars);
                            }

                            lastEffectivePort = "Failed";
                            state.RecordEvent(EdgeEvent(state, currentNode, "Failed", failedEdge, depth));
                            currentNode = workflow.FindNode(failedEdge.ToNodeId)
                                ?? throw new InvalidOperationException(
                                    $"Edge from transform {currentNode.Id} on 'Failed' targets unknown node {failedEdge.ToNodeId}.");
                            currentInput = jsonFailMessage;
                            break;
                        }
                    }

                    // 5. outputScript: can mutate context/workflow vars and override the artifact
                    //    via setOutput. In JSON mode the override is re-validated.
                    var transformFinal = rendered;
                    if (!string.IsNullOrWhiteSpace(currentNode.OutputScript))
                    {
                        var transformOutputScriptOutcome = RunTransformOutputScript(
                            workflow, currentNode, rendered, isJsonMode,
                            contextVars, workflowVars,
                            reviewRound, maxRounds, depth, state, cancellationToken);
                        if (transformOutputScriptOutcome.FailureReason is not null)
                        {
                            var failedEdge = workflow.FindNext(currentNode.Id, "Failed");
                            if (failedEdge is null)
                            {
                                return DryRunWalkResult.Failed(transformOutputScriptOutcome.FailureReason, contextVars, workflowVars);
                            }
                            lastEffectivePort = "Failed";
                            state.RecordEvent(EdgeEvent(state, currentNode, "Failed", failedEdge, depth));
                            currentNode = workflow.FindNode(failedEdge.ToNodeId)
                                ?? throw new InvalidOperationException(
                                    $"Edge from transform {currentNode.Id} on 'Failed' targets unknown node {failedEdge.ToNodeId}.");
                            currentInput = transformOutputScriptOutcome.FailureReason;
                            break;
                        }
                        transformFinal = transformOutputScriptOutcome.Output;
                    }

                    state.RecordEvent(new DryRunEvent(
                        Ordinal: state.NextOrdinal(),
                        Kind: DryRunEventKind.TransformRendered,
                        NodeId: currentNode.Id,
                        NodeKind: currentNode.Kind.ToString(),
                        AgentKey: null,
                        PortName: "Out",
                        Message: null,
                        InputPreview: Preview(currentInput),
                        OutputPreview: Preview(transformFinal),
                        ReviewRound: reviewRound,
                        MaxRounds: maxRounds,
                        SubflowDepth: depth,
                        SubflowKey: null,
                        SubflowVersion: null,
                        Logs: null,
                        DecisionPayload: null));

                    lastEffectivePort = "Out";
                    var transformEdge = workflow.FindNext(currentNode.Id, "Out");
                    if (transformEdge is null)
                    {
                        return CompleteWorkflow(workflow, currentNode, "Out", transformFinal, contextVars, workflowVars, state);
                    }
                    state.RecordEvent(EdgeEvent(state, currentNode, "Out", transformEdge, depth));
                    currentNode = workflow.FindNode(transformEdge.ToNodeId)
                        ?? throw new InvalidOperationException(
                            $"Edge from transform {currentNode.Id} on 'Out' targets unknown node {transformEdge.ToNodeId}.");
                    currentInput = transformFinal;
                    break;
                }

                case WorkflowNodeKind.Swarm:
                    // sc-43: Swarm nodes are non-replayable. The saga executes them by dispatching
                    // N+1 (or N+2) agent calls in sequence, each emerging from the same Swarm node
                    // ID; replay-with-edit can't substitute prior outputs and the dry-run executor
                    // doesn't invoke live LLMs to re-run them. Fail the walk with a clear message —
                    // authors can branch upstream of the swarm if they need to vary inputs.
                    state.RecordEvent(new DryRunEvent(
                        Ordinal: state.NextOrdinal(),
                        Kind: DryRunEventKind.Diagnostic,
                        NodeId: currentNode.Id,
                        NodeKind: currentNode.Kind.ToString(),
                        AgentKey: null,
                        PortName: null,
                        Message: "Swarm nodes are non-replayable; dry-run / replay-with-edit cannot simulate them.",
                        InputPreview: Preview(currentInput),
                        OutputPreview: null,
                        ReviewRound: reviewRound,
                        MaxRounds: maxRounds,
                        SubflowDepth: depth,
                        SubflowKey: null,
                        SubflowVersion: null,
                        Logs: null,
                        DecisionPayload: null));
                    return DryRunWalkResult.Failed(
                        $"Swarm node {currentNode.Id} cannot be dry-run (non-replayable). "
                        + "Replay re-runs swarm bodies fresh; this dry-run path doesn't invoke live LLMs.",
                        contextVars, workflowVars);

                default:
                    return DryRunWalkResult.Failed(
                        $"Unknown node kind {currentNode.Kind} on node {currentNode.Id}.",
                        contextVars, workflowVars);
            }
        }

        return DryRunWalkResult.StepLimitExceeded(
            $"Workflow '{workflow.Key}' v{workflow.Version} exceeded the {MaxStepsPerRun}-step dry-run cap.",
            contextVars, workflowVars);
    }

    /// <summary>
    /// Recursively walk a ReviewLoop subflow up to <c>ReviewMaxRounds</c> times, exiting on the
    /// first round whose terminal port is NOT the configured loop decision (default: "Rejected").
    /// </summary>
    private async Task<DryRunWalkResult> ExecuteReviewLoopAsync(
        WorkflowNode loopNode,
        Workflow subflow,
        string? input,
        IReadOnlyDictionary<string, JsonElement> contextVars,
        IReadOnlyDictionary<string, JsonElement> workflowVars,
        int depth,
        DryRunState state,
        CancellationToken cancellationToken)
    {
        var maxRounds = loopNode.ReviewMaxRounds
            ?? throw new InvalidOperationException(
                $"ReviewLoop node {loopNode.Id} has no ReviewMaxRounds configured.");
        var loopDecision = string.IsNullOrWhiteSpace(loopNode.LoopDecision)
            ? "Rejected"
            : loopNode.LoopDecision!.Trim();

        var roundContext = new Dictionary<string, JsonElement>(contextVars, StringComparer.Ordinal);
        var roundWorkflow = new Dictionary<string, JsonElement>(workflowVars, StringComparer.Ordinal);
        var roundInput = input;

        for (var round = 1; round <= maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.RecordEvent(new DryRunEvent(
                Ordinal: state.NextOrdinal(),
                Kind: DryRunEventKind.LoopIteration,
                NodeId: loopNode.Id,
                NodeKind: loopNode.Kind.ToString(),
                AgentKey: null,
                PortName: null,
                Message: $"ReviewLoop round {round}/{maxRounds} (loopDecision='{loopDecision}').",
                InputPreview: Preview(roundInput),
                OutputPreview: null,
                ReviewRound: round,
                MaxRounds: maxRounds,
                SubflowDepth: depth,
                SubflowKey: subflow.Key,
                SubflowVersion: subflow.Version,
                Logs: null,
                DecisionPayload: null));

            var roundResult = await WalkWorkflowAsync(
                subflow,
                roundInput,
                roundContext,
                roundWorkflow,
                depth,
                reviewRound: round,
                maxRounds: maxRounds,
                state,
                cancellationToken);

            // Persist mutations across rounds — same semantics as the production saga.
            roundContext = new Dictionary<string, JsonElement>(roundResult.ContextVars, StringComparer.Ordinal);
            roundWorkflow = new Dictionary<string, JsonElement>(roundResult.WorkflowVars, StringComparer.Ordinal);
            roundInput = roundResult.FinalArtifact;

            if (roundResult.Terminal == DryRunTerminalState.HitlReached
                || roundResult.Terminal == DryRunTerminalState.Failed
                || roundResult.Terminal == DryRunTerminalState.StepLimitExceeded)
            {
                return roundResult;
            }

            var port = roundResult.TerminalPort ?? "Completed";

            // P3: when the round terminated on the configured loopDecision port AND the parent
            // ReviewLoop has rejection-history enabled, append this round's artifact to the
            // framework-managed __loop.rejectionHistory accumulator before deciding iterate vs.
            // exhaust. Mirrors the saga's AccumulateRejectionHistoryAsync ordering — both the
            // round-that-iterates and the final exhausted round contribute to the history.
            if (string.Equals(port, loopDecision, StringComparison.Ordinal)
                && loopNode.RejectionHistory is { Enabled: true } rejectionConfig)
            {
                roundWorkflow.TryGetValue(RejectionHistoryAccumulator.WorkflowVariableKey, out var existingElement);
                var existingValue = existingElement.ValueKind == JsonValueKind.String
                    ? existingElement.GetString()
                    : existingElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                        ? null
                        : existingElement.GetRawText();
                var artifactBody = roundResult.FinalArtifact ?? string.Empty;
                var updated = RejectionHistoryAccumulator.Append(existingValue, round, artifactBody, rejectionConfig);
                roundWorkflow[RejectionHistoryAccumulator.WorkflowVariableKey] =
                    JsonSerializer.SerializeToElement(updated);
                state.RecordEvent(new DryRunEvent(
                    Ordinal: state.NextOrdinal(),
                    Kind: DryRunEventKind.BuiltinApplied,
                    NodeId: loopNode.Id,
                    NodeKind: loopNode.Kind.ToString(),
                    AgentKey: null,
                    PortName: port,
                    Message: $"P3 rejection-history: appended round {round} ({artifactBody.Length} chars; total {updated.Length} chars).",
                    InputPreview: null,
                    OutputPreview: null,
                    ReviewRound: round,
                    MaxRounds: maxRounds,
                    SubflowDepth: depth,
                    SubflowKey: subflow.Key,
                    SubflowVersion: subflow.Version,
                    Logs: null,
                    DecisionPayload: null));
            }

            // If the round terminated on the configured loop port AND we still have rounds, loop.
            if (string.Equals(port, loopDecision, StringComparison.Ordinal))
            {
                if (round < maxRounds)
                {
                    continue;
                }
                state.RecordEvent(new DryRunEvent(
                    Ordinal: state.NextOrdinal(),
                    Kind: DryRunEventKind.LoopExhausted,
                    NodeId: loopNode.Id,
                    NodeKind: loopNode.Kind.ToString(),
                    AgentKey: null,
                    PortName: "Exhausted",
                    Message: $"ReviewLoop exhausted after {maxRounds} round(s) on '{loopDecision}'.",
                    InputPreview: null,
                    OutputPreview: Preview(roundInput),
                    ReviewRound: round,
                    MaxRounds: maxRounds,
                    SubflowDepth: depth,
                    SubflowKey: subflow.Key,
                    SubflowVersion: subflow.Version,
                    Logs: null,
                    DecisionPayload: null));
                return new DryRunWalkResult(
                    Terminal: DryRunTerminalState.Completed,
                    TerminalPort: "Exhausted",
                    FailureReason: null,
                    FinalArtifact: roundInput,
                    HitlPayload: null,
                    ContextVars: roundContext,
                    WorkflowVars: roundWorkflow);
            }

            // Anything else propagates as the loop's terminal port.
            return new DryRunWalkResult(
                Terminal: DryRunTerminalState.Completed,
                TerminalPort: port,
                FailureReason: null,
                FinalArtifact: roundInput,
                HitlPayload: null,
                ContextVars: roundContext,
                WorkflowVars: roundWorkflow);
        }

        // Defensive: should be unreachable when maxRounds >= 1.
        return DryRunWalkResult.Failed(
            $"ReviewLoop {loopNode.Id} exited the round loop without producing a result.",
            roundContext, roundWorkflow);
    }

    private static DryRunWalkResult CompleteWorkflow(
        Workflow workflow,
        WorkflowNode terminalNode,
        string terminalPort,
        string? finalArtifact,
        IReadOnlyDictionary<string, JsonElement> contextVars,
        IReadOnlyDictionary<string, JsonElement> workflowVars,
        DryRunState state)
    {
        state.RecordEvent(new DryRunEvent(
            Ordinal: state.NextOrdinal(),
            Kind: DryRunEventKind.WorkflowCompleted,
            NodeId: terminalNode.Id,
            NodeKind: terminalNode.Kind.ToString(),
            AgentKey: terminalNode.AgentKey,
            PortName: terminalPort,
            Message: $"Workflow '{workflow.Key}' v{workflow.Version} terminated on port '{terminalPort}'.",
            InputPreview: null,
            OutputPreview: Preview(finalArtifact),
            ReviewRound: null,
            MaxRounds: null,
            SubflowDepth: null,
            SubflowKey: null,
            SubflowVersion: null,
            Logs: null,
            DecisionPayload: null));

        return new DryRunWalkResult(
            Terminal: DryRunTerminalState.Completed,
            TerminalPort: terminalPort,
            FailureReason: null,
            FinalArtifact: finalArtifact,
            HitlPayload: null,
            ContextVars: contextVars,
            WorkflowVars: workflowVars);
    }

    private static DryRunEvent EdgeEvent(
        DryRunState state,
        WorkflowNode fromNode,
        string port,
        WorkflowEdge edge,
        int depth) =>
        new(
            Ordinal: state.NextOrdinal(),
            Kind: DryRunEventKind.EdgeTraversed,
            NodeId: fromNode.Id,
            NodeKind: fromNode.Kind.ToString(),
            AgentKey: fromNode.AgentKey,
            PortName: port,
            Message: $"→ node {edge.ToNodeId}",
            InputPreview: null,
            OutputPreview: null,
            ReviewRound: null,
            MaxRounds: null,
            SubflowDepth: depth,
            SubflowKey: null,
            SubflowVersion: null,
            Logs: null,
            DecisionPayload: null);

    private static JsonElement ParseInputAsJson(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return JsonSerializer.SerializeToElement(string.Empty);
        }

        try
        {
            using var doc = JsonDocument.Parse(input);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Treat opaque text as a JSON string so Logic-node scripts can still read it as
            // `input`. Matches the saga's handling for plain-text artifacts.
            return JsonSerializer.SerializeToElement(input);
        }
    }

    /// <summary>
    /// Saga-parity input-script invocation for Start/Agent/Hitl nodes. Runs the node's
    /// <c>InputScript</c> if present, applies any setWorkflow / setContext mutations to the
    /// passed-in dictionaries, and returns the (possibly script-overridden) input artifact.
    /// </summary>
    private InputScriptOutcome RunInputScriptIfPresent(
        Workflow workflow,
        WorkflowNode node,
        string? input,
        Dictionary<string, JsonElement> contextVars,
        Dictionary<string, JsonElement> workflowVars,
        int? reviewRound,
        int? maxRounds,
        int depth,
        DryRunState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(node.InputScript))
        {
            return new InputScriptOutcome(input, null);
        }

        var inputJson = ParseArtifactAsJsonElement(input);
        var eval = logicScriptHost.Evaluate(
            workflowKey: workflow.Key,
            workflowVersion: workflow.Version,
            nodeId: node.Id,
            script: node.InputScript!,
            declaredPorts: node.OutputPorts,
            input: inputJson,
            context: contextVars,
            cancellationToken: cancellationToken,
            workflow: workflowVars,
            reviewRound: reviewRound,
            reviewMaxRounds: maxRounds,
            allowOutputOverride: false,
            allowInputOverride: true,
            requireSetNodePath: false);

        state.RecordEvent(new DryRunEvent(
            Ordinal: state.NextOrdinal(),
            Kind: DryRunEventKind.LogicEvaluated,
            NodeId: node.Id,
            NodeKind: node.Kind.ToString(),
            AgentKey: node.AgentKey,
            PortName: null,
            Message: eval.Failure is null
                ? $"Input script ran ({eval.LogEntries.Count} log entr{(eval.LogEntries.Count == 1 ? "y" : "ies")})."
                : $"Input script failed ({eval.Failure}): {eval.FailureMessage}",
            InputPreview: Preview(input),
            OutputPreview: eval.InputOverride is null ? null : Preview(eval.InputOverride),
            ReviewRound: reviewRound,
            MaxRounds: maxRounds,
            SubflowDepth: depth,
            SubflowKey: null,
            SubflowVersion: null,
            Logs: eval.LogEntries,
            DecisionPayload: null));

        if (eval.Failure is not null)
        {
            return new InputScriptOutcome(
                null,
                $"Input script for node {node.Id} failed ({eval.Failure}): {eval.FailureMessage}");
        }

        foreach (var (k, v) in eval.ContextUpdates)
        {
            contextVars[k] = v;
        }
        foreach (var (k, v) in eval.WorkflowUpdates)
        {
            workflowVars[k] = v;
        }

        var effective = eval.InputOverride ?? input;
        return new InputScriptOutcome(effective, null);
    }

    /// <summary>
    /// Saga-parity output-script invocation for Agent nodes. The script sees the agent's
    /// message body wrapped as <c>output</c> (with decision metadata injected), can call
    /// setWorkflow / setContext to mutate the shared bag, setNodePath to override the routing
    /// port, and setOutput to override the artifact text. Mirrors
    /// <see cref="WorkflowSagaStateMachine.ResolveSourcePortAsync"/>'s output-script branch.
    /// </summary>
    private OutputScriptOutcome RunAgentOutputScript(
        Workflow workflow,
        WorkflowNode node,
        string output,
        string mockDecision,
        JsonNode? mockPayload,
        Dictionary<string, JsonElement> contextVars,
        Dictionary<string, JsonElement> workflowVars,
        int? reviewRound,
        int? maxRounds,
        int depth,
        DryRunState state,
        CancellationToken cancellationToken)
    {
        var artifactJson = ParseArtifactAsJsonElement(output);
        var payloadElement = mockPayload is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(mockPayload);
        var scriptInput = ComposeAgentScriptInput(artifactJson, mockDecision, payloadElement);

        var eval = logicScriptHost.Evaluate(
            workflowKey: workflow.Key,
            workflowVersion: workflow.Version,
            nodeId: node.Id,
            script: node.OutputScript!,
            declaredPorts: node.OutputPorts,
            input: scriptInput,
            context: contextVars,
            cancellationToken: cancellationToken,
            workflow: workflowVars,
            reviewRound: reviewRound,
            reviewMaxRounds: maxRounds,
            allowOutputOverride: true,
            inputVariableName: "output",
            requireSetNodePath: false);

        state.RecordEvent(new DryRunEvent(
            Ordinal: state.NextOrdinal(),
            Kind: DryRunEventKind.LogicEvaluated,
            NodeId: node.Id,
            NodeKind: node.Kind.ToString(),
            AgentKey: node.AgentKey,
            PortName: eval.OutputPortName,
            Message: eval.Failure is null
                ? $"Output script ran (port='{eval.OutputPortName ?? mockDecision}', override={(eval.OutputOverride is null ? "no" : $"yes, {eval.OutputOverride.Length} chars")})."
                : $"Output script failed ({eval.Failure}): {eval.FailureMessage}",
            InputPreview: Preview(output),
            OutputPreview: Preview(eval.OutputOverride),
            ReviewRound: reviewRound,
            MaxRounds: maxRounds,
            SubflowDepth: depth,
            SubflowKey: null,
            SubflowVersion: null,
            Logs: eval.LogEntries,
            DecisionPayload: null));

        if (eval.Failure is not null)
        {
            return new OutputScriptOutcome(
                mockDecision,
                output,
                false,
                $"Output script for node {node.Id} failed ({eval.Failure}): {eval.FailureMessage}");
        }

        foreach (var (k, v) in eval.ContextUpdates)
        {
            contextVars[k] = v;
        }
        foreach (var (k, v) in eval.WorkflowUpdates)
        {
            workflowVars[k] = v;
        }

        var resolvedPort = string.IsNullOrWhiteSpace(eval.OutputPortName) ? mockDecision : eval.OutputPortName!;
        var resolvedOutput = eval.OutputOverride ?? output;
        return new OutputScriptOutcome(resolvedPort, resolvedOutput, eval.OutputOverride is not null, null);
    }

    /// <summary>
    /// Saga-parity output-script invocation for Subflow / ReviewLoop boundary nodes. Runs once
    /// after the child terminates with the effective port resolved (for ReviewLoop, after the
    /// loop has fully exited, never per iteration). The script sees the child's terminal port as
    /// <c>output.decision</c> and may call <c>setNodePath</c> to reroute, <c>setOutput</c> to
    /// rewrite the artifact propagated downstream, and <c>setContext</c>/<c>setWorkflow</c> to
    /// mutate the parent's bags. Mirrors the saga's
    /// <see cref="WorkflowSagaStateMachine.TryEvaluateBoundaryOutputScriptAsync"/> hook.
    /// </summary>
    private BoundaryOutputScriptOutcome RunBoundaryOutputScript(
        Workflow workflow,
        WorkflowNode boundaryNode,
        string output,
        string effectivePort,
        Dictionary<string, JsonElement> contextVars,
        Dictionary<string, JsonElement> workflowVars,
        int? reviewRound,
        int? maxRounds,
        int depth,
        DryRunState state,
        CancellationToken cancellationToken)
    {
        // Centralized boundary-script port set: author-declared OutputPorts + implicit Failed
        // (+ synthesized Exhausted and resolved LoopDecision for ReviewLoop). Saga, DryRun, and
        // the editor's /validate-script all share BoundaryScriptPorts.GetDeclaredPorts so the
        // wirable set never drifts between dev-time validation and runtime enforcement.
        var declaredPorts = BoundaryScriptPorts.GetDeclaredPorts(boundaryNode);

        var artifactJson = ParseArtifactAsJsonElement(output);
        var scriptInput = ComposeAgentScriptInput(artifactJson, effectivePort, decisionPayload: null);

        var eval = logicScriptHost.Evaluate(
            workflowKey: workflow.Key,
            workflowVersion: workflow.Version,
            nodeId: boundaryNode.Id,
            script: boundaryNode.OutputScript!,
            declaredPorts: declaredPorts,
            input: scriptInput,
            context: contextVars,
            cancellationToken: cancellationToken,
            workflow: workflowVars,
            reviewRound: reviewRound,
            reviewMaxRounds: maxRounds,
            allowOutputOverride: true,
            inputVariableName: "output",
            requireSetNodePath: false);

        state.RecordEvent(new DryRunEvent(
            Ordinal: state.NextOrdinal(),
            Kind: DryRunEventKind.LogicEvaluated,
            NodeId: boundaryNode.Id,
            NodeKind: boundaryNode.Kind.ToString(),
            AgentKey: null,
            PortName: eval.OutputPortName,
            Message: eval.Failure is null
                ? $"Boundary output script ran (port='{eval.OutputPortName ?? effectivePort}', override={(eval.OutputOverride is null ? "no" : $"yes, {eval.OutputOverride.Length} chars")})."
                : $"Boundary output script failed ({eval.Failure}): {eval.FailureMessage}",
            InputPreview: Preview(output),
            OutputPreview: Preview(eval.OutputOverride),
            ReviewRound: reviewRound,
            MaxRounds: maxRounds,
            SubflowDepth: depth,
            SubflowKey: boundaryNode.SubflowKey,
            SubflowVersion: boundaryNode.SubflowVersion,
            Logs: eval.LogEntries,
            DecisionPayload: null));

        if (eval.Failure is not null)
        {
            return new BoundaryOutputScriptOutcome(
                effectivePort,
                output,
                $"Output script for node {boundaryNode.Id} failed ({eval.Failure}): {eval.FailureMessage}");
        }

        foreach (var (k, v) in eval.ContextUpdates)
        {
            contextVars[k] = v;
        }
        foreach (var (k, v) in eval.WorkflowUpdates)
        {
            workflowVars[k] = v;
        }

        var resolvedPort = string.IsNullOrWhiteSpace(eval.OutputPortName) ? effectivePort : eval.OutputPortName!;
        var resolvedOutput = eval.OutputOverride ?? output;
        return new BoundaryOutputScriptOutcome(resolvedPort, resolvedOutput, null);
    }

    /// <summary>
    /// Saga-parity output-script invocation for Transform nodes. The script sees the rendered
    /// text as <c>output</c> — parsed JSON in <c>outputType=json</c> mode, plain string in string
    /// mode — and may call setWorkflow / setContext to mutate vars and setOutput to override the
    /// artifact text. In JSON mode an override is re-validated; an invalid JSON override surfaces
    /// as a script failure (mirrors the saga's <c>ApplyTransformOutputScriptAsync</c> branch).
    /// </summary>
    private TransformOutputScriptOutcome RunTransformOutputScript(
        Workflow workflow,
        WorkflowNode node,
        string rendered,
        bool jsonMode,
        Dictionary<string, JsonElement> contextVars,
        Dictionary<string, JsonElement> workflowVars,
        int? reviewRound,
        int? maxRounds,
        int depth,
        DryRunState state,
        CancellationToken cancellationToken)
    {
        JsonElement scriptInput;
        if (jsonMode)
        {
            using var doc = JsonDocument.Parse(rendered);
            scriptInput = doc.RootElement.Clone();
        }
        else
        {
            scriptInput = JsonSerializer.SerializeToElement(rendered);
        }

        var eval = logicScriptHost.Evaluate(
            workflowKey: workflow.Key,
            workflowVersion: workflow.Version,
            nodeId: node.Id,
            script: node.OutputScript!,
            declaredPorts: node.OutputPorts,
            input: scriptInput,
            context: contextVars,
            cancellationToken: cancellationToken,
            workflow: workflowVars,
            reviewRound: reviewRound,
            reviewMaxRounds: maxRounds,
            allowOutputOverride: true,
            inputVariableName: "output",
            requireSetNodePath: false);

        state.RecordEvent(new DryRunEvent(
            Ordinal: state.NextOrdinal(),
            Kind: DryRunEventKind.LogicEvaluated,
            NodeId: node.Id,
            NodeKind: node.Kind.ToString(),
            AgentKey: null,
            PortName: eval.OutputPortName,
            Message: eval.Failure is null
                ? $"Transform output script ran (override={(eval.OutputOverride is null ? "no" : $"yes, {eval.OutputOverride.Length} chars")})."
                : $"Transform output script failed ({eval.Failure}): {eval.FailureMessage}",
            InputPreview: Preview(rendered),
            OutputPreview: Preview(eval.OutputOverride),
            ReviewRound: reviewRound,
            MaxRounds: maxRounds,
            SubflowDepth: depth,
            SubflowKey: null,
            SubflowVersion: null,
            Logs: eval.LogEntries,
            DecisionPayload: null));

        if (eval.Failure is not null)
        {
            return new TransformOutputScriptOutcome(
                rendered,
                $"Transform node {node.Id} output script failed ({eval.Failure}): {eval.FailureMessage}");
        }

        foreach (var (k, v) in eval.ContextUpdates)
        {
            contextVars[k] = v;
        }
        foreach (var (k, v) in eval.WorkflowUpdates)
        {
            workflowVars[k] = v;
        }

        var finalText = string.IsNullOrEmpty(eval.OutputOverride) ? rendered : eval.OutputOverride!;

        if (jsonMode && eval.OutputOverride is not null)
        {
            try
            {
                using var _ = JsonDocument.Parse(finalText);
            }
            catch (JsonException ex)
            {
                return new TransformOutputScriptOutcome(
                    rendered,
                    $"Transform node {node.Id} output script setOutput value is not valid JSON (outputType=json): {ex.Message}");
            }
        }

        return new TransformOutputScriptOutcome(finalText, null);
    }

    /// <summary>
    /// v3: render the agent's <c>decisionOutputTemplates[port]</c> entry (or the wildcard <c>*</c>
    /// fallback) against the saga's <c>{ decision, outputPortName, output, input, context, workflow }</c>
    /// scope. Delegates resolution + rendering to the shared <see cref="IDecisionTemplateRenderer"/>
    /// so saga and dry-run can't drift on Scriban semantics or fallback rules. Skipped silently
    /// when the agent has no templates or the agent config can't be loaded — the dry-run is
    /// best-effort by design and a missing agent config in the dry-run repo is not a fatal
    /// authoring error.
    /// </summary>
    private async Task<DecisionOutputTemplateOutcome> TryApplyDecisionOutputTemplateAsync(
        WorkflowNode node,
        string mockDecision,
        string effectivePort,
        string effectiveOutput,
        string? currentInput,
        IReadOnlyDictionary<string, JsonElement> contextVars,
        IReadOnlyDictionary<string, JsonElement> workflowVars,
        int? reviewRound,
        int? maxRounds,
        int depth,
        DryRunState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(node.AgentKey) || node.AgentVersion is not int agentVersion)
        {
            return DecisionOutputTemplateOutcome.None;
        }

        AgentConfig agentConfig;
        try
        {
            agentConfig = await agentConfigRepository!.GetAsync(
                node.AgentKey!, agentVersion, cancellationToken);
        }
        catch (AgentConfigNotFoundException)
        {
            return DecisionOutputTemplateOutcome.None;
        }
        catch (Exception)
        {
            // The dry-run runs against an in-memory test repo in unit tests; a fake that throws
            // anything other than AgentConfigNotFoundException should not crash the executor.
            return DecisionOutputTemplateOutcome.None;
        }

        var inputs = new DecisionTemplateInputs(
            DecisionName: mockDecision,
            EffectivePortName: effectivePort,
            OutputText: effectiveOutput,
            OutputJson: ParseArtifactAsJsonElement(effectiveOutput),
            InputJson: currentInput is null ? null : ParseArtifactAsJsonElement(currentInput),
            ContextInputs: contextVars,
            WorkflowInputs: workflowVars);

        var result = decisionTemplateRenderer!.Render(agentConfig, inputs, cancellationToken);
        switch (result)
        {
            case DecisionTemplateRenderResult.Skipped:
                return DecisionOutputTemplateOutcome.None;
            case DecisionTemplateRenderResult.Failed failed:
                return new DecisionOutputTemplateOutcome(
                    null,
                    $"Decision output template for node {node.Id} (port '{effectivePort}') failed: {failed.Reason}");
            case DecisionTemplateRenderResult.Rendered rendered:
                state.RecordEvent(new DryRunEvent(
                    Ordinal: state.NextOrdinal(),
                    Kind: DryRunEventKind.BuiltinApplied,
                    NodeId: node.Id,
                    NodeKind: node.Kind.ToString(),
                    AgentKey: node.AgentKey,
                    PortName: effectivePort,
                    Message: $"Decision-output template applied on '{effectivePort}' ({rendered.Text.Length} chars).",
                    InputPreview: null,
                    OutputPreview: Preview(rendered.Text),
                    ReviewRound: reviewRound,
                    MaxRounds: maxRounds,
                    SubflowDepth: depth,
                    SubflowKey: null,
                    SubflowVersion: null,
                    Logs: null,
                    DecisionPayload: null));
                return new DecisionOutputTemplateOutcome(rendered.Text, null);
            default:
                throw new InvalidOperationException($"Unexpected render result: {result.GetType()}");
        }
    }

    /// <summary>
    /// v3: build the HITL suspension payload, surfacing the agent's <c>outputTemplate</c> (legacy
    /// single-template form rendered client-side in hitl-review) and any structured
    /// <c>decisionOutputTemplates</c> the saga applies on submit. When templates are present and
    /// the renderer is wired, run a best-effort server render against
    /// <c>{ input, context, workflow }</c> with empty form-field values so authors can spot
    /// template typos at design time. Render failure surfaces in <see cref="DryRunHitlPayload.RenderError"/>
    /// instead of failing the whole dry-run — the form may legitimately rely on field values not
    /// available until a human submits.
    /// </summary>
    private async Task<DryRunHitlPayload> BuildHitlPayloadAsync(
        WorkflowNode node,
        string? input,
        IReadOnlyDictionary<string, JsonElement> contextVars,
        IReadOnlyDictionary<string, JsonElement> workflowVars,
        CancellationToken cancellationToken)
    {
        var basePayload = new DryRunHitlPayload(
            NodeId: node.Id,
            AgentKey: node.AgentKey ?? string.Empty,
            Input: input);

        if (agentConfigRepository is null
            || templateRenderer is null
            || string.IsNullOrWhiteSpace(node.AgentKey)
            || node.AgentVersion is not int agentVersion)
        {
            return basePayload;
        }

        AgentConfig agentConfig;
        try
        {
            agentConfig = await agentConfigRepository.GetAsync(
                node.AgentKey!, agentVersion, cancellationToken);
        }
        catch (AgentConfigNotFoundException)
        {
            return basePayload;
        }
        catch (Exception)
        {
            return basePayload;
        }

        var outputTemplate = ReadOutputTemplateFromConfigJson(agentConfig.ConfigJson);
        var decisionOutputTemplates = agentConfig.Configuration.DecisionOutputTemplates;

        // Pick the template most likely to render usefully at suspension. Prefer the singular
        // outputTemplate (the form preview the human would see); fall back to the first per-port
        // decisionOutputTemplate or the wildcard.
        var renderTemplate = outputTemplate;
        var renderPort = node.OutputPorts.FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(renderTemplate) && decisionOutputTemplates is { Count: > 0 })
        {
            renderTemplate = DecisionTemplateRenderer.ResolveTemplate(decisionOutputTemplates, renderPort);
        }

        string? renderedPreview = null;
        string? renderError = null;
        if (!string.IsNullOrWhiteSpace(renderTemplate))
        {
            var inputJson = input is null ? (JsonElement?)null : ParseArtifactAsJsonElement(input);
            var scope = DecisionOutputTemplateContext.BuildForHitl(
                decision: renderPort,
                outputPortName: renderPort,
                fieldValues: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                reason: null,
                reasons: null,
                actions: null,
                contextInputs: contextVars,
                workflowInputs: workflowVars);
            // BuildForHitl exposes form fields under `input.*`. The legacy outputTemplate often
            // also references `{{ input }}` to print the upstream artifact; populate that here so
            // the preview matches what the hitl-review component renders client-side.
            if (inputJson is { } parsed)
            {
                scope["upstreamInput"] = ConvertJsonElementToScriptValue(parsed);
                // Most legacy templates write `{{ input }}` expecting the upstream artifact text,
                // not the form fields. Shadow `input` with the upstream value so the preview
                // reflects what the human would actually see.
                scope["input"] = parsed.ValueKind == JsonValueKind.Object
                        && parsed.TryGetProperty("text", out var textProp)
                        && textProp.ValueKind == JsonValueKind.String
                        && parsed.EnumerateObject().Count() == 1
                    ? textProp.GetString() ?? string.Empty
                    : ConvertJsonElementToScriptValue(parsed);
            }

            try
            {
                renderedPreview = templateRenderer.Render(renderTemplate!, scope, cancellationToken);
            }
            catch (PromptTemplateException ex)
            {
                renderError = ex.Message;
            }
        }

        return basePayload with
        {
            OutputTemplate = outputTemplate,
            DecisionOutputTemplates = decisionOutputTemplates,
            RenderedFormPreview = renderedPreview,
            RenderError = renderError,
        };
    }

    private static string? ReadOutputTemplateFromConfigJson(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return doc.RootElement.TryGetProperty("outputTemplate", out var element)
                && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object ConvertJsonElementToScriptValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.TryGetInt64(out var i) ? i : (object)element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            // For objects/arrays, serialize back to JSON so Scriban can index into them via
            // member access — the renderer's `EnableRelaxedMemberAccess` makes this resilient.
            _ => element.GetRawText(),
        };
    }

    private readonly record struct DecisionOutputTemplateOutcome(string? OverrideOutput, string? FailureReason)
    {
        public static readonly DecisionOutputTemplateOutcome None = new(null, null);
    }

    /// <summary>
    /// Wrap an artifact text as a <see cref="JsonElement"/> matching saga semantics: parse as
    /// JSON if valid, else wrap as <c>{ "text": "…" }</c>. Plain-text artifacts thus expose
    /// <c>input.text</c> / <c>output.text</c> to scripts byte-for-byte the way the saga does.
    /// </summary>
    private static JsonElement ParseArtifactAsJsonElement(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { text });
        }
    }

    /// <summary>
    /// Mirror of <c>WorkflowSagaStateMachine.ComposeAgentScriptInput</c>: builds the
    /// <c>output</c> object the agent output script sees, injecting decision/decisionKind/
    /// outputPortName/decisionPayload alongside the artifact's own fields.
    /// </summary>
    private static JsonElement ComposeAgentScriptInput(
        JsonElement artifactJson,
        string decisionPortName,
        JsonElement? decisionPayload)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (artifactJson.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in artifactJson.EnumerateObject())
                {
                    if (property.Name is "decision" or "decisionKind" or "outputPortName" or "decisionPayload")
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

            writer.WriteString("decision", decisionPortName);
            writer.WriteString("decisionKind", decisionPortName);
            writer.WriteString("outputPortName", decisionPortName);
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

    private readonly record struct InputScriptOutcome(string? EffectiveInput, string? FailureReason);

    private readonly record struct OutputScriptOutcome(
        string Port,
        string Output,
        bool SetOutputCalled,
        string? FailureReason);

    private readonly record struct BoundaryOutputScriptOutcome(
        string ResolvedPort,
        string? ResolvedOutput,
        string? FailureReason);

    private readonly record struct TransformOutputScriptOutcome(string Output, string? FailureReason);

    private static string? Preview(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        const int max = 2048;
        return text.Length <= max ? text : text[..max] + "…";
    }

    private sealed record DryRunWalkResult(
        DryRunTerminalState Terminal,
        string? TerminalPort,
        string? FailureReason,
        string? FinalArtifact,
        DryRunHitlPayload? HitlPayload,
        IReadOnlyDictionary<string, JsonElement> ContextVars,
        IReadOnlyDictionary<string, JsonElement> WorkflowVars)
    {
        public static DryRunWalkResult Failed(
            string reason,
            IReadOnlyDictionary<string, JsonElement> context,
            IReadOnlyDictionary<string, JsonElement> workflowVars) =>
            new(DryRunTerminalState.Failed, null, reason, null, null, context, workflowVars);

        public static DryRunWalkResult StepLimitExceeded(
            string reason,
            IReadOnlyDictionary<string, JsonElement> context,
            IReadOnlyDictionary<string, JsonElement> workflowVars) =>
            new(DryRunTerminalState.StepLimitExceeded, null, reason, null, null, context, workflowVars);
    }

    private sealed class DryRunState
    {
        private readonly Dictionary<string, Queue<DryRunMockResponse>> mocks;
        private readonly List<DryRunEvent> events = new();
        private int ordinal;

        public DryRunState(IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>> source)
        {
            mocks = new Dictionary<string, Queue<DryRunMockResponse>>(StringComparer.Ordinal);
            foreach (var (agentKey, list) in source)
            {
                mocks[agentKey] = new Queue<DryRunMockResponse>(list);
            }
        }

        public IReadOnlyList<DryRunEvent> Events => events;

        public int NextOrdinal() => Interlocked.Increment(ref ordinal);

        public void RecordEvent(DryRunEvent ev) => events.Add(ev);

        public bool TryDequeueMock(string agentKey, out DryRunMockResponse? response)
        {
            if (mocks.TryGetValue(agentKey, out var queue) && queue.Count > 0)
            {
                response = queue.Dequeue();
                return true;
            }
            response = null;
            return false;
        }
    }
}

