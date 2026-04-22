using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeFlow.Runtime.Observability;

namespace CodeFlow.Runtime;

public sealed class InvocationLoop
{
    private static readonly ToolSchema SubmitTool = new(
        "submit",
        "Submit the final decision for this agent invocation.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["decision"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("completed", "approved", "approved_with_actions", "rejected")
                },
                ["payload"] = new JsonObject()
            },
            ["required"] = new JsonArray("decision")
        });

    private static readonly ToolSchema FailTool = new(
        "fail",
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
        var externalTools = toolRegistry.AvailableTools(request.ToolAccessPolicy);
        var toolsByName = externalTools
            .Concat([SubmitTool, FailTool])
            .ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        var toolCatalog = externalTools
            .Concat([SubmitTool, FailTool])
            .ToArray();

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
            lastAssistantOutput = response.Message.Content;

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
                return new InvocationLoopResult(
                    lastAssistantOutput,
                    new CompletedDecision(),
                    transcript,
                    aggregateTokenUsage,
                    totalToolCalls);
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

                if (TryHandleTerminalTool(toolCall, out var terminalResult, out var terminalError))
                {
                    if (terminalResult is not null)
                    {
                        if (observer is not null)
                        {
                            await observer.OnToolCallCompletedAsync(
                                toolCall,
                                new ToolResult(toolCall.Id, $"[{toolCall.Name}]"),
                                cancellationToken);
                        }

                        return new InvocationLoopResult(
                            lastAssistantOutput,
                            terminalResult,
                            transcript,
                            aggregateTokenUsage,
                            totalToolCalls);
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
        out AgentDecision? terminalDecision,
        out ToolResult? terminalError)
    {
        terminalDecision = null;
        terminalError = null;

        if (string.Equals(toolCall.Name, SubmitTool.Name, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                terminalDecision = ParseSubmittedDecision(toolCall.Arguments);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
            {
                terminalError = new ToolResult(toolCall.Id, exception.Message, IsError: true);
            }

            return true;
        }

        if (string.Equals(toolCall.Name, FailTool.Name, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                terminalDecision = new FailedDecision(GetRequiredString(toolCall.Arguments, "reason"));
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
            {
                terminalError = new ToolResult(toolCall.Id, exception.Message, IsError: true);
            }

            return true;
        }

        return false;
    }

    private static AgentDecision ParseSubmittedDecision(JsonNode? arguments)
    {
        var decision = GetRequiredString(arguments, "decision").ToLowerInvariant();
        var payload = arguments?["payload"]?.DeepClone();

        return decision switch
        {
            "completed" => new CompletedDecision(payload),
            "approved" => new ApprovedDecision(payload),
            "approved_with_actions" => new ApprovedWithActionsDecision(
                GetRequiredStringArray(payload, "actions"),
                payload),
            "rejected" => new RejectedDecision(
                GetRequiredStringArray(payload, "reasons"),
                payload),
            _ => throw new InvalidOperationException($"Unsupported submit decision '{decision}'.")
        };
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

    private static IReadOnlyList<string> GetRequiredStringArray(JsonNode? node, string propertyName)
    {
        if (node?[propertyName] is not JsonArray array)
        {
            throw new InvalidOperationException($"The '{propertyName}' payload field is required.");
        }

        var values = array
            .Select(item => item is JsonValue value && value.TryGetValue<string>(out var text)
                ? text?.Trim()
                : null)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        if (values.Length == 0)
        {
            throw new InvalidOperationException($"The '{propertyName}' payload field must contain at least one value.");
        }

        return values;
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
        return new InvocationLoopResult(
            output,
            new FailedDecision(reason),
            transcript,
            tokenUsage,
            toolCallsExecuted);
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
