using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;

namespace CodeFlow.Orchestration.DryRun;

/// <summary>
/// In-process workflow walker that simulates a real run without invoking the LLM, the bus, or
/// the saga checkpointing layer. Designed for the T1 dry-run UX — the author clicks "run" and
/// gets a sub-second deterministic trace of which nodes would fire and with what artifacts.
///
/// Scope notes (v1):
/// <list type="bullet">
///   <item><description>Supports node kinds Start, Agent, Logic, Hitl, Subflow, ReviewLoop.</description></item>
///   <item><description>Reuses <see cref="LogicNodeScriptHost"/> for Logic-node script execution
///     so script semantics match the saga byte-for-byte.</description></item>
///   <item><description>Honors the new built-ins: P4 (mirror output to workflow var) and P5
///     (output-port replacements). RejectionHistory accumulation is NOT yet honored — fixtures
///     authored against the new ReviewLoop pair scaffold should still produce a meaningful trace
///     since rejection-history reads are graceful when the variable is missing.</description></item>
///   <item><description>Does NOT execute author-attached input/output scripts on agent nodes,
///     decision-output templates, or retry-context handoffs. These are documented as v1
///     limitations on the trace itself (a Diagnostic event is emitted).</description></item>
///   <item><description>Does NOT render agent prompt templates; the trace records the input
///     artifact a node receives, which is what authors typically want to inspect anyway.</description></item>
/// </list>
/// </summary>
public sealed class DryRunExecutor
{
    public const int MaxStepsPerRun = 256;

    private readonly IWorkflowRepository workflowRepository;
    private readonly LogicNodeScriptHost logicScriptHost;

    public DryRunExecutor(
        IWorkflowRepository workflowRepository,
        LogicNodeScriptHost logicScriptHost)
    {
        this.workflowRepository = workflowRepository ?? throw new ArgumentNullException(nameof(workflowRepository));
        this.logicScriptHost = logicScriptHost ?? throw new ArgumentNullException(nameof(logicScriptHost));
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

                    if (!state.TryDequeueMock(currentNode.AgentKey!, out var mock))
                    {
                        return DryRunWalkResult.Failed(
                            $"No mock response queued for agent '{currentNode.AgentKey}' (workflow '{workflow.Key}' v{workflow.Version}).",
                            contextVars, workflowVars);
                    }

                    var output = mock!.Output ?? string.Empty;

                    // P4: mirror agent output text into the named workflow variable BEFORE
                    // routing, so downstream Logic nodes / port replacements observe it.
                    if (!string.IsNullOrWhiteSpace(currentNode.MirrorOutputToWorkflowVar))
                    {
                        workflowVars[currentNode.MirrorOutputToWorkflowVar!] =
                            JsonSerializer.SerializeToElement(output);
                        state.RecordEvent(new DryRunEvent(
                            Ordinal: state.NextOrdinal(),
                            Kind: DryRunEventKind.BuiltinApplied,
                            NodeId: currentNode.Id,
                            NodeKind: currentNode.Kind.ToString(),
                            AgentKey: currentNode.AgentKey,
                            PortName: null,
                            Message: $"P4 mirror: workflow.{currentNode.MirrorOutputToWorkflowVar} ← agent output ({output.Length} chars).",
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

                    // P5: if the port has a configured replacement, swap the artifact for the
                    // named workflow variable's contents.
                    if (currentNode.OutputPortReplacements is not null
                        && currentNode.OutputPortReplacements.TryGetValue(effectivePort, out var replacementVarName)
                        && workflowVars.TryGetValue(replacementVarName, out var replacementValue))
                    {
                        effectiveOutput = replacementValue.ValueKind == JsonValueKind.String
                            ? replacementValue.GetString() ?? string.Empty
                            : replacementValue.GetRawText();
                        state.RecordEvent(new DryRunEvent(
                            Ordinal: state.NextOrdinal(),
                            Kind: DryRunEventKind.BuiltinApplied,
                            NodeId: currentNode.Id,
                            NodeKind: currentNode.Kind.ToString(),
                            AgentKey: currentNode.AgentKey,
                            PortName: effectivePort,
                            Message: $"P5 replace: artifact on '{effectivePort}' ← workflow.{replacementVarName} ({effectiveOutput.Length} chars).",
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
                    state.RecordEvent(new DryRunEvent(
                        Ordinal: state.NextOrdinal(),
                        Kind: DryRunEventKind.HitlSuspended,
                        NodeId: currentNode.Id,
                        NodeKind: currentNode.Kind.ToString(),
                        AgentKey: currentNode.AgentKey,
                        PortName: null,
                        Message: "Dry-run halted at HITL node; form would be presented to a human reviewer.",
                        InputPreview: Preview(currentInput),
                        OutputPreview: null,
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
                        HitlPayload: new DryRunHitlPayload(
                            currentNode.Id,
                            currentNode.AgentKey ?? string.Empty,
                            currentInput),
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
                        return CompleteWorkflow(workflow, currentNode, subTerminalPort, subResult.FinalArtifact, contextVars, workflowVars, state);
                    }
                    state.RecordEvent(EdgeEvent(state, currentNode, subTerminalPort, subEdge, depth));
                    currentNode = workflow.FindNode(subEdge.ToNodeId)
                        ?? throw new InvalidOperationException(
                            $"Edge from subflow {currentNode.Id} on '{subTerminalPort}' targets unknown node {subEdge.ToNodeId}.");
                    currentInput = subResult.FinalArtifact;
                    break;
                }

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

