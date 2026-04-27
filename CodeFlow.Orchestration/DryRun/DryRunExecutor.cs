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
/// Scope notes (v2):
/// <list type="bullet">
///   <item><description>Supports node kinds Start, Agent, Logic, Hitl, Subflow, ReviewLoop.</description></item>
///   <item><description>Reuses <see cref="LogicNodeScriptHost"/> for Logic-node script execution
///     AND for author-attached input scripts (Start/Agent/Hitl) and output scripts (Agent), so
///     script semantics match the saga byte-for-byte.</description></item>
///   <item><description>Honors the built-ins: P3 (rejection-history accumulation in ReviewLoop
///     bodies), P4 (mirror output to workflow var), and P5 (output-port replacements).</description></item>
///   <item><description>Does NOT yet apply decision-output templates, retry-context handoffs for
///     Failed-port self-loops, or render HITL form templates at suspension. These are documented
///     v2 limitations; a Diagnostic event surfaces the gap on the trace.</description></item>
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
                        lastEffectivePort = effectivePort;
                    }

                    // P5: if the port has a configured replacement, swap the artifact for the
                    // named workflow variable's contents. Runs AFTER the output script so the
                    // script-managed variable updates are visible.
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
                    var hitlInputOutcome = RunInputScriptIfPresent(
                        workflow, currentNode, currentInput, contextVars, workflowVars,
                        reviewRound, maxRounds, depth, state, cancellationToken);
                    if (hitlInputOutcome.FailureReason is not null)
                    {
                        return DryRunWalkResult.Failed(hitlInputOutcome.FailureReason, contextVars, workflowVars);
                    }
                    currentInput = hitlInputOutcome.EffectiveInput;

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
        return new OutputScriptOutcome(resolvedPort, resolvedOutput, null);
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

    private readonly record struct OutputScriptOutcome(string Port, string Output, string? FailureReason);

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

