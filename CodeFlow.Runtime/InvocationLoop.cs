using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Runtime.Observability;

namespace CodeFlow.Runtime;

public sealed class InvocationLoop
{
    public const string SubmitToolName = "submit";
    public const string FailToolName = "fail";
    public const string SetContextToolName = "setContext";
    public const string SetGlobalToolName = "setGlobal";

    /// <summary>
    /// Default port name emitted when an agent has no declared outputs (e.g., legacy Logic-only
    /// flows or test fixtures that skip declaring outputs). Using the implicit-Failed port for
    /// the no-outputs case would be misleading on a successful submit; the canonical
    /// <c>Completed</c> port is the safe default.
    /// </summary>
    private const string DefaultPortName = "Completed";

    /// <summary>
    /// Cap on the serialized size of accumulated <c>setContext</c> / <c>setGlobal</c> writes per
    /// invocation. Mirrors the cap on <see cref="ContextAssembler"/>'s Logic-node counterpart so
    /// agents and Logic nodes share the same bag-write budget. Exceeding the cap returns a tool
    /// error and the loop continues — the offending value is not persisted.
    /// </summary>
    private const int MaxContextUpdatesChars = 256 * 1024;

    /// <summary>
    /// Cap on the length of a single <c>setContext</c> / <c>setGlobal</c> key, mirroring the
    /// Logic-node validation guard. Keys above this length are rejected with a tool error.
    /// </summary>
    private const int MaxContextKeyChars = 256;

    private static readonly ToolSchema FailTool = new(
        FailToolName,
        "Fail the current agent invocation with a reason.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["reason"] = new JsonObject
                {
                    ["type"] = "string"
                }
            },
            ["required"] = new JsonArray("reason")
        });

    private static readonly ToolSchema SetContextTool = new(
        SetContextToolName,
        "Persist a value into the workflow context bag under the given key. Visible to "
        + "downstream agents as `{{ context.<key> }}` in templates and `context.<key>` in "
        + "Logic-node scripts. Updates accumulate during this invocation and are committed "
        + "atomically once `submit` completes; if the agent fails, pending writes are discarded.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["key"] = new JsonObject { ["type"] = "string" },
                ["value"] = new JsonObject()
            },
            ["required"] = new JsonArray("key", "value")
        });

    private static readonly ToolSchema SetGlobalTool = new(
        SetGlobalToolName,
        "Persist a value into the cross-saga global bag under the given key. Same lifecycle "
        + "rules as `setContext` — committed on successful `submit`, discarded on failure.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["key"] = new JsonObject { ["type"] = "string" },
                ["value"] = new JsonObject()
            },
            ["required"] = new JsonArray("key", "value")
        });

    private static ToolSchema BuildSubmitTool(IReadOnlyList<string> declaredPortNames)
    {
        ArgumentNullException.ThrowIfNull(declaredPortNames);

        var properties = new JsonObject
        {
            ["payload"] = new JsonObject()
        };

        var decisionSchema = new JsonObject
        {
            ["type"] = "string"
        };

        if (declaredPortNames.Count > 0)
        {
            var enumArray = new JsonArray();
            foreach (var name in declaredPortNames)
            {
                enumArray.Add(name);
            }
            decisionSchema["enum"] = enumArray;
            decisionSchema["description"] =
                "The output port to route this invocation to. Must match one of the agent's "
                + "declared output port names.";
        }
        else
        {
            decisionSchema["description"] =
                "The output port to route this invocation to. The agent has no declared outputs; "
                + $"omit this field to default to '{DefaultPortName}'.";
        }

        properties["decision"] = decisionSchema;

        var required = declaredPortNames.Count > 0
            ? new JsonArray("decision")
            : new JsonArray();

        return new ToolSchema(
            SubmitToolName,
            "Submit the final decision for this agent invocation.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required
            });
    }

    private readonly IModelClient modelClient;
    private readonly ToolRegistry toolRegistry;
    private readonly Func<DateTimeOffset> nowProvider;

    public InvocationLoop(
        IModelClient modelClient,
        ToolRegistry toolRegistry,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
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
        var declaredPortNames = (request.DeclaredOutputs ?? [])
            .Select(o => o.Kind)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var submitTool = BuildSubmitTool(declaredPortNames);
        var externalTools = toolRegistry.AvailableTools(request.ToolAccessPolicy);
        var runtimeTools = new[] { submitTool, FailTool, SetContextTool, SetGlobalTool };
        var toolsByName = externalTools
            .Concat(runtimeTools)
            .ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        var toolCatalog = externalTools
            .Concat(runtimeTools)
            .ToArray();

        // Pending writes — accumulated by setContext/setGlobal calls and applied to the result
        // only when the loop terminates with a non-Failed decision. Failed terminations discard
        // them so a botched invocation doesn't corrupt the saga's bag.
        var pendingContextUpdates = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var pendingGlobalUpdates = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        var totalToolCalls = 0;
        var consecutiveNonMutatingCalls = 0;
        var roundNumber = 0;
        TokenUsage? aggregateTokenUsage = null;
        string lastAssistantOutput = string.Empty;

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
            if (observer is not null)
            {
                await observer.OnModelCallStartedAsync(roundNumber, cancellationToken);
            }

            var response = await modelClient.InvokeAsync(
                new InvocationRequest(
                    transcript,
                    toolCatalog,
                    request.Model,
                    request.MaxTokens,
                    request.Temperature),
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
                    roundNumber,
                    response.Message,
                    response.TokenUsage,
                    aggregateTokenUsage,
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
                    pendingGlobalUpdates);
            }

            foreach (var toolCall in response.Message.ToolCalls)
            {
                totalToolCalls++;

                if (totalToolCalls > budget.MaxToolCalls)
                {
                    return BuildFailureResult(
                        transcript,
                        lastAssistantOutput,
                        InvocationLoopFailureReasons.ToolCallBudgetExceeded,
                        aggregateTokenUsage,
                        totalToolCalls);
                }

                if (HasExceededDuration(startedAt, budget))
                {
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

                if (TryHandleSetContextLikeTool(
                        toolCall,
                        pendingContextUpdates,
                        pendingGlobalUpdates,
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

                    // setContext / setGlobal are mutating writes — reset the non-mutating streak.
                    consecutiveNonMutatingCalls = 0;
                    continue;
                }

                if (TryHandleTerminalTool(toolCall, declaredPortNames, out var terminalResult, out var terminalError))
                {
                    if (terminalResult is not null)
                    {
                        // Reject terminal submits with empty/whitespace assistant content when
                        // the agent has declared outputs and didn't choose the implicit Failed
                        // port. Without this guard, an LLM that calls `submit` on its very first
                        // turn (no content, just a tool call) produces a 0-byte artifact —
                        // downstream agents see empty input, HITL forms render blank fields, and
                        // the workflow proceeds in a broken state with no diagnostic. Push a
                        // reminder back into the transcript and let the loop continue; the LLM
                        // gets another shot at producing real output.
                        var isFailedPort = string.Equals(
                            terminalResult.PortName, "Failed", StringComparison.Ordinal);
                        if (declaredPortNames.Length > 0
                            && !isFailedPort
                            && string.IsNullOrWhiteSpace(lastAssistantOutput))
                        {
                            // Without this tool message, the next round's request would carry the
                            // assistant's `function_call` (the submit) with no matching
                            // `function_call_output`, and the OpenAI Responses API rejects the
                            // request: "No tool output found for function call <id>". Provider
                            // protocol requires every prior `function_call` to be paired with a
                            // `function_call_output` before the next assistant turn.
                            transcript.Add(new ChatMessage(
                                ChatMessageRole.Tool,
                                "missing assistant content; retry",
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
                                    new ToolResult(toolCall.Id, "missing assistant content; retry", IsError: true),
                                    cancellationToken);
                            }
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

                        if (observer is not null)
                        {
                            await observer.OnToolCallCompletedAsync(
                                toolCall,
                                new ToolResult(toolCall.Id, $"[{toolCall.Name}]"),
                                cancellationToken);
                        }

                        return BuildTerminalResult(
                            lastAssistantOutput,
                            terminalResult,
                            transcript,
                            aggregateTokenUsage,
                            totalToolCalls,
                            pendingContextUpdates,
                            pendingGlobalUpdates);
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

                var toolResult = await InvokeToolAsync(
                    toolCall,
                    request.ToolAccessPolicy,
                    cancellationToken,
                    request.ToolExecutionContext);
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
                    return BuildFailureResult(
                        transcript,
                        lastAssistantOutput,
                        InvocationLoopFailureReasons.ConsecutiveNonMutatingCallsExceeded,
                        aggregateTokenUsage,
                        totalToolCalls);
                }
            }
        }
    }

    private async Task<ToolResult> InvokeToolAsync(
        ToolCall toolCall,
        ToolAccessPolicy? policy,
        CancellationToken cancellationToken,
        ToolExecutionContext? context)
    {
        using var activity = CodeFlowActivity.StartChild("tool.call");
        activity?.SetTag(CodeFlowActivity.TagNames.ToolName, toolCall.Name);

        try
        {
            var result = await toolRegistry.InvokeAsync(toolCall, policy, cancellationToken, context);
            if (result.IsError)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "tool reported error");
            }
            else
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            return new ToolResult(toolCall.Id, exception.Message, IsError: true);
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
        // Pending setContext/setGlobal writes are deliberately NOT applied — failed invocations
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
        IReadOnlyDictionary<string, JsonElement> globalUpdates)
    {
        return new InvocationLoopResult(
            output,
            decision,
            transcript,
            tokenUsage,
            toolCallsExecuted,
            contextUpdates.Count > 0 ? contextUpdates : null,
            globalUpdates.Count > 0 ? globalUpdates : null);
    }

    private static InvocationLoopResult BuildTerminalResult(
        string output,
        AgentDecision decision,
        IReadOnlyList<ChatMessage> transcript,
        TokenUsage? tokenUsage,
        int toolCallsExecuted,
        IReadOnlyDictionary<string, JsonElement> contextUpdates,
        IReadOnlyDictionary<string, JsonElement> globalUpdates)
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
            globalUpdates);
    }

    private static bool TryHandleSetContextLikeTool(
        ToolCall toolCall,
        Dictionary<string, JsonElement> pendingContextUpdates,
        Dictionary<string, JsonElement> pendingGlobalUpdates,
        out ToolResult? toolResult)
    {
        Dictionary<string, JsonElement>? targetBag = null;
        string? toolDisplayName = null;

        if (string.Equals(toolCall.Name, SetContextToolName, StringComparison.OrdinalIgnoreCase))
        {
            targetBag = pendingContextUpdates;
            toolDisplayName = SetContextToolName;
        }
        else if (string.Equals(toolCall.Name, SetGlobalToolName, StringComparison.OrdinalIgnoreCase))
        {
            targetBag = pendingGlobalUpdates;
            toolDisplayName = SetGlobalToolName;
        }

        if (targetBag is null || toolDisplayName is null)
        {
            toolResult = null;
            return false;
        }

        try
        {
            var keyNode = toolCall.Arguments?["key"];
            if (keyNode is not JsonValue keyValue
                || !keyValue.TryGetValue<string>(out var key)
                || string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException($"{toolDisplayName}(key, value) requires a non-empty string key.");
            }

            if (key.Length > MaxContextKeyChars)
            {
                throw new InvalidOperationException(
                    $"{toolDisplayName} key length {key.Length} exceeds the {MaxContextKeyChars}-character cap.");
            }

            if (string.Equals(toolDisplayName, SetGlobalToolName, StringComparison.Ordinal)
                && ProtectedGlobals.IsReserved(key))
            {
                throw new InvalidOperationException(
                    $"setGlobal('{key}', ...) is rejected: '{key}' is a framework-managed global "
                    + "and cannot be overwritten by agents.");
            }

            var valueNode = toolCall.Arguments?["value"];
            // Treat an explicit null value as a delete — store the JSON null so saga merge
            // overwrites the previous value with null. Missing 'value' is an error.
            if (valueNode is null && (toolCall.Arguments is null || !toolCall.Arguments.AsObject().ContainsKey("value")))
            {
                throw new InvalidOperationException($"{toolDisplayName}(key, value) requires a 'value' argument.");
            }

            var element = valueNode is null
                ? JsonDocument.Parse("null").RootElement.Clone()
                : JsonDocument.Parse(valueNode.ToJsonString()).RootElement.Clone();

            var candidate = new Dictionary<string, JsonElement>(targetBag, StringComparer.Ordinal)
            {
                [key] = element
            };
            var serializedSize = JsonSerializer.Serialize(candidate).Length;
            if (serializedSize > MaxContextUpdatesChars)
            {
                throw new InvalidOperationException(
                    $"{toolDisplayName} writes total {serializedSize} chars, exceeding the "
                    + $"{MaxContextUpdatesChars}-character cap. Discarding this write.");
            }

            targetBag[key] = element;
            toolResult = new ToolResult(toolCall.Id, $"[{toolDisplayName}({key})]");
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException or JsonException)
        {
            toolResult = new ToolResult(toolCall.Id, exception.Message, IsError: true);
            return true;
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
