using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

public sealed class InvocationLoop
{
    public const string SubmitToolName = "submit";
    public const string FailToolName = "fail";
    public const string SetContextToolName = "setContext";
    public const string SetWorkflowToolName = "setWorkflow";

    /// <summary>
    /// Default port name emitted when an agent has no declared outputs (e.g., legacy Logic-only
    /// flows or test fixtures that skip declaring outputs). Using the implicit-Failed port for
    /// the no-outputs case would be misleading on a successful submit; the canonical
    /// <c>Completed</c> port is the safe default.
    /// </summary>
    internal const string DefaultPortName = "Completed";

    private readonly IModelClient modelClient;
    private readonly ToolRegistry toolRegistry;
    private readonly ToolInvoker toolInvoker;
    private readonly Func<DateTimeOffset> nowProvider;

    public InvocationLoop(
        IModelClient modelClient,
        ToolRegistry toolRegistry,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        this.toolInvoker = new ToolInvoker(toolRegistry);
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<InvocationLoopResult> RunAsync(
        InvocationLoopRequest request,
        CancellationToken cancellationToken = default)
        => RunAsync(request, observer: null, cancellationToken);

    public async Task<InvocationLoopResult> RunAsync(
        InvocationLoopRequest request,
        IInvocationObserver? observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var budget = request.Budget ?? InvocationLoopBudget.Default;
        var startedAt = nowProvider();
        var transcript = request.Messages.ToList();
        var declaredOutputs = (request.DeclaredOutputs ?? [])
            .Where(static o => !string.IsNullOrWhiteSpace(o.Kind))
            .GroupBy(o => o.Kind, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToArray();
        var declaredPortNames = declaredOutputs.Select(o => o.Kind).ToArray();
        // Per-port content-optional opt-out (V2). Sentinel ports like Cancelled/Skip whose
        // decision carries the meaning may declare ContentOptional=true so the empty-content
        // submit guard skips them.
        var contentOptionalByPort = declaredOutputs
            .Where(o => o.ContentOptional)
            .ToDictionary(o => o.Kind, _ => true, StringComparer.Ordinal);
        var submitTool = ToolSchemaBuilder.BuildSubmitTool(declaredPortNames);
        var externalTools = toolRegistry.AvailableTools(request.ToolAccessPolicy);
        var runtimeTools = new[] { submitTool, ToolSchemaBuilder.FailTool, ToolSchemaBuilder.SetContextTool, ToolSchemaBuilder.SetWorkflowTool };
        var toolsByName = externalTools
            .Concat(runtimeTools)
            .ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        var toolCatalog = externalTools
            .Concat(runtimeTools)
            .ToArray();

        // Pending writes — accumulated by setContext/setWorkflow calls and applied to the result
        // only when the loop terminates with a non-Failed decision. Failed terminations discard
        // them so a botched invocation doesn't corrupt the saga's bag.
        var pendingContextUpdates = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var pendingWorkflowUpdates = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        var totalToolCalls = 0;
        var consecutiveNonMutatingCalls = 0;
        var roundNumber = 0;
        TokenUsage? aggregateTokenUsage = null;
        string lastAssistantOutput = string.Empty;
        var softWarnFired = false;
        var hardWarnFired = false;

        while (true)
        {
            if (HasExceededDuration(startedAt, budget))
            {
                return BuildFailureResult(
                    transcript,
                    lastAssistantOutput,
                    InvocationLoopFailureReasons.LoopDurationExceeded,
                    aggregateTokenUsage,
                    totalToolCalls);
            }

            roundNumber++;
            // One Guid per LLM round-trip. Stable correlator for token-usage records and any
            // downstream observability that needs to attribute a captured event to a specific
            // call. Provider-native ids (Anthropic `id`, OpenAI response id) aren't surfaced by
            // our model clients, so we mint our own to stay provider-agnostic.
            var invocationId = Guid.NewGuid();
            if (observer is not null)
            {
                await observer.OnModelCallStartedAsync(invocationId, roundNumber, cancellationToken);
            }

            // Hard precondition: every prior assistant `function_call` in the transcript must
            // have a matching Tool-role `function_call_output`. Both OpenAI Responses and
            // Anthropic enforce this; sending an orphaned call produces opaque provider errors
            // ("No tool output found for function call <id>"). Catch it client-side so the
            // stack trace points at the offending retry path rather than the HTTP layer.
            EnsureToolCallPairing(transcript);

            var response = await modelClient.InvokeAsync(
                new InvocationRequest(
                    transcript,
                    toolCatalog,
                    request.Model,
                    request.MaxTokens,
                    request.Temperature,
                    invocationId),
                cancellationToken);

            aggregateTokenUsage = SumTokenUsage(aggregateTokenUsage, response.TokenUsage);
            transcript.Add(response.Message);
            // Track the most recent NON-EMPTY assistant content as the artifact output. When an
            // LLM splits its turn across multiple rounds (e.g., `setContext` → tool result →
            // `submit`), the final round's `Content` is often empty because providers omit
            // narration alongside terminal tool calls. Overwriting unconditionally would
            // clobber the substantive content (the question, the PRD, the review) produced in
            // an earlier round. Keeping the last non-empty content makes the artifact stable
            // regardless of how the model paces its tool calls.
            if (!string.IsNullOrEmpty(response.Message.Content))
            {
                lastAssistantOutput = response.Message.Content;
            }

            if (observer is not null)
            {
                await observer.OnModelCallCompletedAsync(
                    invocationId,
                    roundNumber,
                    response.Message,
                    response.TokenUsage,
                    aggregateTokenUsage,
                    request.Provider,
                    request.Model,
                    response.RawUsage,
                    cancellationToken);
            }

            if (response.Message.ToolCalls is not { Count: > 0 })
            {
                if (declaredPortNames.Length > 0)
                {
                    // The agent has declared outputs, but the LLM ended its turn without
                    // calling `submit` (or any tool). Defaulting to a hardcoded "Completed"
                    // here would route to a port that isn't in the declared set, which
                    // violates the port-model invariant and silently bypasses downstream
                    // routing (the workflow appears to "complete" without ever reaching the
                    // intended next node). Push a reminder back into the transcript and let
                    // the loop continue; repeated non-compliance hits the
                    // consecutive-non-mutating budget and fails cleanly with a clear reason.
                    transcript.Add(new ChatMessage(
                        ChatMessageRole.User,
                        "You must call the `submit` tool to terminate this invocation. "
                        + "`decision` must be one of: "
                        + string.Join(", ", declaredPortNames)
                        + ". Write your output as the assistant message content, then call `submit`."));
                    consecutiveNonMutatingCalls++;
                    if (consecutiveNonMutatingCalls > budget.MaxConsecutiveNonMutatingCalls)
                    {
                        return BuildFailureResult(
                            transcript,
                            lastAssistantOutput,
                            InvocationLoopFailureReasons.ConsecutiveNonMutatingCallsExceeded,
                            aggregateTokenUsage,
                            totalToolCalls);
                    }
                    continue;
                }

                // No declared outputs — legacy / test fixture path. Default to Completed for
                // backwards compatibility with agents that don't declare a port set.
                return BuildSuccessResult(
                    lastAssistantOutput,
                    new AgentDecision("Completed"),
                    transcript,
                    aggregateTokenUsage,
                    totalToolCalls,
                    pendingContextUpdates,
                    pendingWorkflowUpdates);
            }

            foreach (var toolCall in response.Message.ToolCalls)
            {
                totalToolCalls++;

                if (totalToolCalls > budget.MaxToolCalls)
                {
                    DrainOrphanedToolCalls(
                        transcript,
                        "Invocation aborted: ToolCallBudgetExceeded. Tool call was not dispatched.");
                    return BuildFailureResult(
                        transcript,
                        lastAssistantOutput,
                        InvocationLoopFailureReasons.ToolCallBudgetExceeded,
                        aggregateTokenUsage,
                        totalToolCalls);
                }

                if (HasExceededDuration(startedAt, budget))
                {
                    DrainOrphanedToolCalls(
                        transcript,
                        "Invocation aborted: LoopDurationExceeded. Tool call was not dispatched.");
                    return BuildFailureResult(
                        transcript,
                        lastAssistantOutput,
                        InvocationLoopFailureReasons.LoopDurationExceeded,
                        aggregateTokenUsage,
                        totalToolCalls);
                }

                if (observer is not null)
                {
                    await observer.OnToolCallStartedAsync(toolCall, cancellationToken);
                }

                if (ContextUpdateHandler.TryHandle(
                        toolCall,
                        pendingContextUpdates,
                        pendingWorkflowUpdates,
                        out var setResult))
                {
                    transcript.Add(new ChatMessage(
                        ChatMessageRole.Tool,
                        setResult!.Content,
                        ToolCallId: setResult.CallId,
                        IsError: setResult.IsError));

                    if (observer is not null)
                    {
                        await observer.OnToolCallCompletedAsync(toolCall, setResult, cancellationToken);
                    }

                    // setContext / setWorkflow are mutating writes — reset the non-mutating streak.
                    consecutiveNonMutatingCalls = 0;
                    continue;
                }

                if (TryHandleTerminalTool(toolCall, declaredPortNames, out var terminalResult, out var terminalError))
                {
                    if (terminalResult is not null)
                    {
                        // Reject terminal submits with empty/whitespace assistant content when
                        // the agent has declared outputs and didn't choose the implicit Failed
                        // port (or another port the author flagged content-optional). Without
                        // this guard, an LLM that calls `submit` on its very first turn (no
                        // content, just a tool call) produces a 0-byte artifact — downstream
                        // agents see empty input, HITL forms render blank fields, and the
                        // workflow proceeds in a broken state with no diagnostic. Push a
                        // reminder back into the transcript and let the loop continue; the LLM
                        // gets another shot at producing real output.
                        var isFailedPort = string.Equals(
                            terminalResult.PortName, "Failed", StringComparison.Ordinal);
                        var isContentOptionalPort = contentOptionalByPort.ContainsKey(terminalResult.PortName);
                        if (declaredPortNames.Length > 0
                            && !isFailedPort
                            && !isContentOptionalPort
                            && string.IsNullOrWhiteSpace(lastAssistantOutput))
                        {
                            // Without this tool message, the next round's request would carry the
                            // assistant's `function_call` (the submit) with no matching
                            // `function_call_output`, and the OpenAI Responses API rejects the
                            // request: "No tool output found for function call <id>". Provider
                            // protocol requires every prior `function_call` to be paired with a
                            // `function_call_output` before the next assistant turn.
                            var toolErrorMessage =
                                $"Port \"{terminalResult.PortName}\" requires non-empty assistant "
                                + "message content. Write your output as message text BEFORE "
                                + "calling submit.";
                            transcript.Add(new ChatMessage(
                                ChatMessageRole.Tool,
                                toolErrorMessage,
                                ToolCallId: toolCall.Id,
                                IsError: true));
                            transcript.Add(new ChatMessage(
                                ChatMessageRole.User,
                                "You called `submit` without writing any assistant message "
                                + "content. The message content becomes the output artifact "
                                + "handed to the next node. Write your full output (the "
                                + "question, the PRD body, the review, etc.) as the assistant "
                                + "message text, then call `submit` again."));
                            consecutiveNonMutatingCalls++;
                            if (observer is not null)
                            {
                                await observer.OnToolCallCompletedAsync(
                                    toolCall,
                                    new ToolResult(toolCall.Id, toolErrorMessage, IsError: true),
                                    cancellationToken);
                            }
                            if (consecutiveNonMutatingCalls > budget.MaxConsecutiveNonMutatingCalls)
                            {
                                DrainOrphanedToolCalls(
                                    transcript,
                                    "Invocation aborted: ConsecutiveNonMutatingCallsExceeded after submit on empty content. Subsequent tool calls from the same assistant message were not dispatched.");
                                return BuildFailureResult(
                                    transcript,
                                    lastAssistantOutput,
                                    InvocationLoopFailureReasons.ConsecutiveNonMutatingCallsExceeded,
                                    aggregateTokenUsage,
                                    totalToolCalls);
                            }
                            continue;
                        }

                        // Pair the terminal tool call with a synthetic Tool message before
                        // returning. The submit / fail tools are resolved by the InvocationLoop
                        // itself (no dispatched tool produces a real `function_call_output`), so
                        // without this append the transcript ends with an unpaired assistant
                        // `function_call`. That's fine when the caller throws the transcript
                        // away after one invocation, but the Goal-node orchestrator (epic 978)
                        // accumulates each iteration's transcript into the next iteration's
                        // History — every Goal iteration that exits via submit before the goal
                        // is complete leaves the submit call unpaired and breaks the next
                        // iteration's EnsureToolCallPairing guard with
                        // OrphanFunctionCallException. Pin the synthetic content to the same
                        // `[<toolname>]` placeholder the observer overload sees so transcript
                        // consumers + observers stay aligned.
                        var terminalAck = new ToolResult(toolCall.Id, $"[{toolCall.Name}]");
                        transcript.Add(new ChatMessage(
                            ChatMessageRole.Tool,
                            terminalAck.Content,
                            ToolCallId: terminalAck.CallId,
                            IsError: false));

                        if (observer is not null)
                        {
                            await observer.OnToolCallCompletedAsync(
                                toolCall,
                                terminalAck,
                                cancellationToken);
                        }

                        return BuildTerminalResult(
                            lastAssistantOutput,
                            terminalResult,
                            transcript,
                            aggregateTokenUsage,
                            totalToolCalls,
                            pendingContextUpdates,
                            pendingWorkflowUpdates);
                    }

                    if (terminalError is not null)
                    {
                        transcript.Add(new ChatMessage(
                            ChatMessageRole.Tool,
                            terminalError.Content,
                            ToolCallId: terminalError.CallId,
                            IsError: terminalError.IsError));

                        if (observer is not null)
                        {
                            await observer.OnToolCallCompletedAsync(toolCall, terminalError, cancellationToken);
                        }

                        consecutiveNonMutatingCalls++;

                        if (consecutiveNonMutatingCalls > budget.MaxConsecutiveNonMutatingCalls)
                        {
                            DrainOrphanedToolCalls(
                                transcript,
                                "Invocation aborted: ConsecutiveNonMutatingCallsExceeded after terminal-tool error. Subsequent tool calls from the same assistant message were not dispatched.");
                            return BuildFailureResult(
                                transcript,
                                lastAssistantOutput,
                                InvocationLoopFailureReasons.ConsecutiveNonMutatingCallsExceeded,
                                aggregateTokenUsage,
                                totalToolCalls);
                        }
                    }

                    continue;
                }

                // sc-680: per-invocation wrapped context exposes StageWorkflowBagWrite to host
                // tools (e.g. setup_workspace) so they can stage workflow-bag writes through the
                // same caps + reserved-key check that the setWorkflow tool path enforces.
                var hostToolContext = ContextUpdateHandler.WrapForHostTool(
                    request.ToolExecutionContext,
                    pendingWorkflowUpdates);

                var toolResult = await toolInvoker.InvokeAsync(
                    toolCall,
                    request.ToolAccessPolicy,
                    cancellationToken,
                    hostToolContext);
                transcript.Add(new ChatMessage(
                    ChatMessageRole.Tool,
                    toolResult.Content,
                    ToolCallId: toolResult.CallId,
                    IsError: toolResult.IsError));

                if (observer is not null)
                {
                    await observer.OnToolCallCompletedAsync(toolCall, toolResult, cancellationToken);
                }

                var isMutating = toolsByName.TryGetValue(toolCall.Name, out var toolSchema) && toolSchema.IsMutating;
                consecutiveNonMutatingCalls = isMutating
                    ? 0
                    : consecutiveNonMutatingCalls + 1;

                if (consecutiveNonMutatingCalls > budget.MaxConsecutiveNonMutatingCalls)
                {
                    DrainOrphanedToolCalls(
                        transcript,
                        "Invocation aborted: ConsecutiveNonMutatingCallsExceeded after host-tool call. Subsequent tool calls from the same assistant message were not dispatched.");
                    return BuildFailureResult(
                        transcript,
                        lastAssistantOutput,
                        InvocationLoopFailureReasons.ConsecutiveNonMutatingCallsExceeded,
                        aggregateTokenUsage,
                        totalToolCalls);
                }
            }

            MaybeAppendBudgetWarning(
                transcript,
                totalToolCalls,
                budget,
                ref softWarnFired,
                ref hardWarnFired);
        }
    }

    /// <summary>
    /// Appends a transcript trailer when <c>totalToolCalls</c> first crosses the soft- or
    /// hard-warn threshold from <see cref="InvocationLoopBudget"/>. Each threshold fires once
    /// per invocation so the warning lands at the moment of pressure without spamming every
    /// subsequent turn. Either threshold can be disabled by setting its <c>WarnRemaining</c> to
    /// <c>0</c> on the budget.
    /// </summary>
    private static void MaybeAppendBudgetWarning(
        List<ChatMessage> transcript,
        int totalToolCalls,
        InvocationLoopBudget budget,
        ref bool softWarnFired,
        ref bool hardWarnFired)
    {
        var remaining = budget.MaxToolCalls - totalToolCalls;

        if (budget.HardWarnRemaining > 0
            && !hardWarnFired
            && remaining <= budget.HardWarnRemaining)
        {
            transcript.Add(new ChatMessage(
                ChatMessageRole.User,
                $"⚠️ Tool budget critical: {remaining} call(s) remaining of {budget.MaxToolCalls}. "
                + "Your next assistant turn must call `submit` on a declared port — any further "
                + "tool call will exhaust the budget and fail the invocation."));
            hardWarnFired = true;
            softWarnFired = true;
            return;
        }

        if (budget.SoftWarnRemaining > 0
            && !softWarnFired
            && remaining <= budget.SoftWarnRemaining)
        {
            transcript.Add(new ChatMessage(
                ChatMessageRole.User,
                $"⚠️ Tool budget: {totalToolCalls}/{budget.MaxToolCalls} used, {remaining} remaining. "
                + "Finish any necessary edits and call `submit` on a declared port — further "
                + "exploration risks exhausting the budget."));
            softWarnFired = true;
        }
    }

    private static bool TryHandleTerminalTool(
        ToolCall toolCall,
        IReadOnlyList<string> declaredPortNames,
        out AgentDecision? terminalDecision,
        out ToolResult? terminalError)
    {
        terminalDecision = null;
        terminalError = null;

        if (string.Equals(toolCall.Name, SubmitToolName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                terminalDecision = ParseSubmittedDecision(toolCall.Arguments, declaredPortNames);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
            {
                terminalError = new ToolResult(toolCall.Id, exception.Message, IsError: true);
            }

            return true;
        }

        if (string.Equals(toolCall.Name, FailToolName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var reason = GetRequiredString(toolCall.Arguments, "reason");
                terminalDecision = new AgentDecision("Failed", new JsonObject { ["reason"] = reason });
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
            {
                terminalError = new ToolResult(toolCall.Id, exception.Message, IsError: true);
            }

            return true;
        }

        return false;
    }

    private static AgentDecision ParseSubmittedDecision(JsonNode? arguments, IReadOnlyList<string> declaredPortNames)
    {
        var payload = arguments?["payload"]?.DeepClone();
        var decisionNode = arguments?["decision"];

        if (decisionNode is null)
        {
            if (declaredPortNames.Count > 0)
            {
                throw new InvalidOperationException(
                    "The 'decision' argument is required. Allowed values: "
                    + string.Join(", ", declaredPortNames) + ".");
            }
            return new AgentDecision(DefaultPortName, payload);
        }

        if (decisionNode is not JsonValue value
            || !value.TryGetValue<string>(out var decision)
            || string.IsNullOrWhiteSpace(decision))
        {
            throw new InvalidOperationException("The 'decision' argument must be a non-empty string.");
        }

        var trimmed = decision.Trim();

        if (declaredPortNames.Count > 0
            && !declaredPortNames.Contains(trimmed, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Decision '{trimmed}' is not one of the agent's declared output ports. "
                + $"Allowed: {string.Join(", ", declaredPortNames)}.");
        }

        return new AgentDecision(trimmed, payload);
    }

    private static string GetRequiredString(JsonNode? node, string propertyName)
    {
        if (node?[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        throw new InvalidOperationException($"The '{propertyName}' argument is required.");
    }

    private bool HasExceededDuration(DateTimeOffset startedAt, InvocationLoopBudget budget)
    {
        return budget.MaxLoopDuration >= TimeSpan.Zero
            && nowProvider() - startedAt > budget.MaxLoopDuration;
    }

    private static InvocationLoopResult BuildFailureResult(
        IReadOnlyList<ChatMessage> transcript,
        string output,
        string reason,
        TokenUsage? tokenUsage,
        int toolCallsExecuted)
    {
        // Pending setContext/setWorkflow writes are deliberately NOT applied — failed invocations
        // discard their pending bag-writes per Slice 13's lifecycle rule.
        return new InvocationLoopResult(
            output,
            new AgentDecision("Failed", new JsonObject { ["reason"] = reason }),
            transcript,
            tokenUsage,
            toolCallsExecuted);
    }

    private static InvocationLoopResult BuildSuccessResult(
        string output,
        AgentDecision decision,
        IReadOnlyList<ChatMessage> transcript,
        TokenUsage? tokenUsage,
        int toolCallsExecuted,
        IReadOnlyDictionary<string, JsonElement> contextUpdates,
        IReadOnlyDictionary<string, JsonElement> workflowUpdates)
    {
        return new InvocationLoopResult(
            output,
            decision,
            transcript,
            tokenUsage,
            toolCallsExecuted,
            contextUpdates.Count > 0 ? contextUpdates : null,
            workflowUpdates.Count > 0 ? workflowUpdates : null);
    }

    private static InvocationLoopResult BuildTerminalResult(
        string output,
        AgentDecision decision,
        IReadOnlyList<ChatMessage> transcript,
        TokenUsage? tokenUsage,
        int toolCallsExecuted,
        IReadOnlyDictionary<string, JsonElement> contextUpdates,
        IReadOnlyDictionary<string, JsonElement> workflowUpdates)
    {
        // Discard pending updates on a Failed terminal — same lifecycle rule as exception/budget
        // failures. Successful submits commit them.
        if (string.Equals(decision.PortName, "Failed", StringComparison.Ordinal))
        {
            return new InvocationLoopResult(
                output,
                decision,
                transcript,
                tokenUsage,
                toolCallsExecuted);
        }

        return BuildSuccessResult(
            output,
            decision,
            transcript,
            tokenUsage,
            toolCallsExecuted,
            contextUpdates,
            workflowUpdates);
    }

    /// <summary>
    /// Walks <paramref name="transcript"/> and verifies every <c>function_call</c> emitted by an
    /// assistant message has a matching <c>function_call_output</c> Tool message later in the
    /// transcript. Throws <see cref="OrphanFunctionCallException"/> on the first violation.
    /// </summary>
    /// <remarks>
    /// Defensive: the loop's own retry paths already maintain pairing. This guard turns any
    /// future regression into a stack trace pointing at the offending append, instead of an
    /// opaque provider HTTP error.
    /// </remarks>
    public static void EnsureToolCallPairing(IReadOnlyList<ChatMessage> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var orphans = FindOrphanedToolCallIds(transcript);
        if (orphans.Count > 0)
        {
            throw new OrphanFunctionCallException(orphans);
        }
    }

    /// <summary>
    /// Walks <paramref name="transcript"/> and returns the deterministically-ordered list of
    /// tool-call IDs that have no matching <c>function_call_output</c> Tool message. Empty when
    /// the transcript is well-formed. Pure function — used by both
    /// <see cref="EnsureToolCallPairing"/> (which throws on a non-empty result) and the loop's
    /// own mid-batch early-exit drain (which appends error stubs to repair the transcript before
    /// returning a failure result).
    /// </summary>
    public static IReadOnlyList<string> FindOrphanedToolCallIds(IReadOnlyList<ChatMessage> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        // Single forward pass: track outstanding tool-call IDs and tick them off when the
        // matching Tool message appears. Any IDs still outstanding at the end are orphans.
        // O(n) in transcript length; constant memory in unique-ID count.
        var outstanding = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in transcript)
        {
            if (message.Role == ChatMessageRole.Assistant && message.ToolCalls is { Count: > 0 } calls)
            {
                foreach (var call in calls)
                {
                    if (!string.IsNullOrEmpty(call.Id))
                    {
                        outstanding.Add(call.Id);
                    }
                }
            }
            else if (message.Role == ChatMessageRole.Tool && !string.IsNullOrEmpty(message.ToolCallId))
            {
                outstanding.Remove(message.ToolCallId);
            }
        }

        if (outstanding.Count == 0)
        {
            return Array.Empty<string>();
        }
        return outstanding.OrderBy(static id => id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Repair a transcript with unpaired assistant <c>function_call</c>s before the loop returns
    /// a failure result mid-tool-call-batch. The OpenAI Responses API + Anthropic both reject any
    /// transcript that contains an assistant <c>function_call</c> without a matching Tool
    /// <c>function_call_output</c>; the Goal-node orchestrator (epic 978) accumulates each
    /// iteration's transcript into the next iteration's <c>History</c>, so an orphan inside a
    /// failed iteration breaks the next iteration's first model call with
    /// <see cref="OrphanFunctionCallException"/>.
    /// <para/>
    /// Without this drain, every mid-batch early-exit (ToolCallBudgetExceeded /
    /// LoopDurationExceeded / ConsecutiveNonMutatingCallsExceeded fired while the loop was still
    /// iterating an assistant message's <c>ToolCalls</c>) leaves the unprocessed tool calls
    /// unpaired. Observed on 2026-05-13 during qwen3-35b Goal testing: a multi-step task where
    /// qwen emitted several tool calls per assistant turn hit the consecutive-non-mutating
    /// budget mid-batch, exited, and the next Goal iteration's pairing guard threw on the
    /// surfaced orphan ID.
    /// </summary>
    private static void DrainOrphanedToolCalls(List<ChatMessage> transcript, string reason)
    {
        var orphans = FindOrphanedToolCallIds(transcript);
        if (orphans.Count == 0)
        {
            return;
        }

        foreach (var callId in orphans)
        {
            transcript.Add(new ChatMessage(
                ChatMessageRole.Tool,
                reason,
                ToolCallId: callId,
                IsError: true));
        }
    }

    private static TokenUsage? SumTokenUsage(TokenUsage? current, TokenUsage? next)
    {
        return (current, next) switch
        {
            (null, null) => null,
            (null, not null) => next,
            (not null, null) => current,
            _ => new TokenUsage(
                current!.InputTokens + next!.InputTokens,
                current.OutputTokens + next.OutputTokens,
                current.TotalTokens + next.TotalTokens)
        };
    }

}
