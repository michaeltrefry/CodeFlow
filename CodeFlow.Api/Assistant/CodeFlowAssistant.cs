using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace CodeFlow.Api.Assistant;

public interface ICodeFlowAssistant
{
    IAsyncEnumerable<AssistantStreamItem> AskAsync(
        string userMessage,
        IReadOnlyList<AssistantMessage> history,
        ToolAccessPolicy? toolPolicy = null,
        AssistantPageContext? pageContext = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Streaming chat-loop service. Routes requests to the configured provider's official SDK
/// (Anthropic or OpenAI; LMStudio reuses the OpenAI client with a custom endpoint). Each turn
/// runs a bounded tool-calling loop: stream the model's response, dispatch any tool calls,
/// feed the results back, and continue until the model produces a final answer (no more tool
/// calls) or <see cref="AssistantRuntimeConfig.MaxTurns"/> is reached.
/// </summary>
/// <remarks>
/// Mirrors the architecture of CodeGraph's <c>GraphAssistant</c> (provider routing, SDK-native
/// streaming, per-call key/endpoint resolution). Only the FINAL assistant text is persisted to
/// <c>assistant_messages</c> — tool_use / tool_result blocks are transient: they live within the
/// turn so the model can act on them, then are discarded. On the next turn, if the user's new
/// question depends on prior tool data, the model tool-calls again. This keeps the persistence
/// schema simple at the cost of some redundant tool calls across turns.
/// </remarks>
public sealed class CodeFlowAssistant(
    IAssistantSettingsResolver settingsResolver,
    ILlmProviderSettingsRepository providerSettings,
    IAssistantSystemPromptProvider systemPromptProvider,
    AssistantToolDispatcher toolDispatcher,
    IAnthropicClient anthropicClient,
    ILogger<CodeFlowAssistant> logger) : ICodeFlowAssistant
{
    public async IAsyncEnumerable<AssistantStreamItem> AskAsync(
        string userMessage,
        IReadOnlyList<AssistantMessage> history,
        ToolAccessPolicy? toolPolicy = null,
        AssistantPageContext? pageContext = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(history);

        var config = await settingsResolver.ResolveAsync(cancellationToken);
        var systemPrompt = await systemPromptProvider.GetSystemPromptAsync(cancellationToken);
        // HAA-8: prepend a structured page-context block so the model can resolve "this trace",
        // "this node", etc. without the user pasting IDs. The block is per-turn (it changes as
        // the user navigates) and the most recent value wins.
        var contextBlock = AssistantPageContextFormatter.FormatAsSystemMessage(pageContext);
        if (!string.IsNullOrEmpty(contextBlock))
        {
            systemPrompt = string.IsNullOrEmpty(systemPrompt)
                ? contextBlock
                : contextBlock + "\n\n" + systemPrompt;
        }
        var allowedTools = FilterTools(toolDispatcher.Tools, toolPolicy);

        IAsyncEnumerable<AssistantStreamItem> stream = config.Provider switch
        {
            LlmProviderKeys.Anthropic => AskAnthropicAsync(config, systemPrompt, userMessage, history, allowedTools, cancellationToken),
            LlmProviderKeys.OpenAi or LlmProviderKeys.LmStudio => AskOpenAiAsync(config, systemPrompt, userMessage, history, allowedTools, cancellationToken),
            _ => throw new InvalidOperationException(
                $"Assistant provider '{config.Provider}' is not supported. Expected one of: anthropic, openai, lmstudio.")
        };

        await foreach (var item in stream.WithCancellation(cancellationToken))
        {
            yield return item;
        }
    }

    private static IReadOnlyCollection<IAssistantTool> FilterTools(
        IReadOnlyCollection<IAssistantTool> tools,
        ToolAccessPolicy? policy)
    {
        if (policy is null)
        {
            return tools;
        }

        var filtered = tools.Where(t => policy.AllowsTool(t.Name)).ToArray();
        return filtered;
    }

    private async IAsyncEnumerable<AssistantStreamItem> AskAnthropicAsync(
        AssistantRuntimeConfig config,
        string systemPrompt,
        string userMessage,
        IReadOnlyList<AssistantMessage> history,
        IReadOnlyCollection<IAssistantTool> allowedTools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var apiKey = await providerSettings.GetDecryptedApiKeyAsync(LlmProviderKeys.Anthropic, cancellationToken)
            ?? throw new InvalidOperationException("Anthropic API key is not configured.");

        var settings = await providerSettings.GetAsync(LlmProviderKeys.Anthropic, cancellationToken);
        var endpointUrl = settings?.EndpointUrl;

        var client = anthropicClient.WithOptions(opts => opts with
        {
            APIKey = apiKey,
            BaseUrl = string.IsNullOrWhiteSpace(endpointUrl) ? opts.BaseUrl : new Uri(endpointUrl)
        });

        var anthropicTools = allowedTools.Count > 0 ? AnthropicToolMapper.Map(allowedTools) : null;

        var messages = new List<MessageParam>(history.Count + 1);
        foreach (var msg in history)
        {
            if (msg.Role is AssistantMessageRole.System) continue;
            messages.Add(new MessageParam
            {
                Role = msg.Role == AssistantMessageRole.Assistant ? Role.Assistant : Role.User,
                Content = msg.Content
            });
        }
        messages.Add(new MessageParam { Role = Role.User, Content = userMessage });

        MessageDeltaUsage? finalUsage = null;
        for (var turn = 0; turn < config.MaxTurns; turn++)
        {
            var createParams = BuildAnthropicParams(config, systemPrompt, messages, anthropicTools);

            // Per-turn accumulators: text gets streamed back to the caller; tool_use blocks gather
            // their input JSON across multiple InputJSONDelta events keyed by content-block index.
            var assistantTextThisTurn = new StringBuilder();
            var pendingToolUses = new Dictionary<long, PendingAnthropicToolUse>();
            string? stopReason = null;

            await foreach (var ev in client.Messages.CreateStreaming(createParams, cancellationToken))
            {
                if (ev.TryPickContentBlockStart(out var cbStart))
                {
                    if (cbStart.ContentBlock.TryPickToolUse(out var toolUse))
                    {
                        pendingToolUses[cbStart.Index] = new PendingAnthropicToolUse(
                            toolUse.ID,
                            toolUse.Name,
                            new StringBuilder());
                    }
                }
                else if (ev.TryPickContentBlockDelta(out var cbDelta))
                {
                    if (cbDelta.Delta.TryPickText(out var td))
                    {
                        assistantTextThisTurn.Append(td.Text);
                        yield return new AssistantTextDelta(td.Text);
                    }
                    else if (cbDelta.Delta.TryPickInputJSON(out var ijd) &&
                             pendingToolUses.TryGetValue(cbDelta.Index, out var pending))
                    {
                        pending.JsonBuffer.Append(ijd.PartialJSON);
                    }
                }
                else if (ev.TryPickDelta(out var msgDelta))
                {
                    finalUsage = msgDelta.Usage;
                    if (msgDelta.Delta.StopReason.HasValue)
                    {
                        stopReason = msgDelta.Delta.StopReason.Value.ToString();
                    }
                }
            }

            // No tool calls — final answer in hand. Emit usage + done.
            if (pendingToolUses.Count == 0)
            {
                if (SerializeAnthropicUsage(finalUsage) is { } usageJson)
                {
                    yield return new AssistantTokenUsage(LlmProviderKeys.Anthropic, config.Model, usageJson);
                }
                yield return new AssistantTurnDone(LlmProviderKeys.Anthropic, config.Model);
                yield break;
            }

            // Persist usage from this turn before we run tools — token tracking should reflect the
            // model call regardless of how many tool round-trips follow.
            if (SerializeAnthropicUsage(finalUsage) is { } turnUsageJson)
            {
                yield return new AssistantTokenUsage(LlmProviderKeys.Anthropic, config.Model, turnUsageJson);
            }
            finalUsage = null;

            // Echo the assistant turn (text + tool_use blocks) back into history so the model has
            // a coherent transcript when we send tool results.
            var assistantBlocks = new List<ContentBlockParam>();
            if (assistantTextThisTurn.Length > 0)
            {
                assistantBlocks.Add((TextBlockParam)new TextBlockParam { Text = assistantTextThisTurn.ToString() });
            }
            // Order tool uses by content-block index so the transcript matches what the model emitted.
            foreach (var (_, pending) in pendingToolUses.OrderBy(kvp => kvp.Key))
            {
                assistantBlocks.Add((ToolUseBlockParam)new ToolUseBlockParam
                {
                    ID = pending.Id,
                    Name = pending.Name,
                    Input = ParseToolInputAsDictionary(pending.JsonBuffer.ToString()),
                });
            }
            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantBlocks });

            // Dispatch each tool and collect results; emit Started+Completed events so the UI can
            // render in-flight tool cards. A user-role message carrying tool_result blocks goes
            // back to the model on the next iteration.
            var resultBlocks = new List<ContentBlockParam>();
            foreach (var (_, pending) in pendingToolUses.OrderBy(kvp => kvp.Key))
            {
                var args = ParseToolArguments(pending.JsonBuffer.ToString());
                yield return new AssistantToolCallStarted(pending.Id, pending.Name, args);
                var result = await toolDispatcher.InvokeAsync(pending.Name, args, cancellationToken);
                yield return new AssistantToolCallCompleted(pending.Id, pending.Name, result.ResultJson, result.IsError);

                resultBlocks.Add((ToolResultBlockParam)new ToolResultBlockParam
                {
                    ToolUseID = pending.Id,
                    Content = result.ResultJson,
                    IsError = result.IsError ? true : null,
                });
            }

            messages.Add(new MessageParam { Role = Role.User, Content = resultBlocks });

            // Loop continues — model gets another shot with the tool results in context.
            _ = stopReason; // captured for future logging; not used yet
        }

        logger.LogWarning(
            "Anthropic assistant turn hit MaxTurns={MaxTurns} without producing a final answer.",
            config.MaxTurns);
        yield return new AssistantTurnError(
            $"Assistant exceeded the {config.MaxTurns}-turn tool-loop budget without producing a final answer.");
    }

    private static MessageCreateParams BuildAnthropicParams(
        AssistantRuntimeConfig config,
        string systemPrompt,
        List<MessageParam> messages,
        IReadOnlyList<ToolUnion>? tools)
    {
        // System is init-only on MessageCreateParams, so we have to branch on whether to set it
        // and on whether tools are present.
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            return tools is null
                ? new MessageCreateParams { Model = config.Model, MaxTokens = config.MaxTokens, Messages = messages }
                : new MessageCreateParams
                {
                    Model = config.Model,
                    MaxTokens = config.MaxTokens,
                    Messages = messages,
                    Tools = tools.ToList()
                };
        }

        return tools is null
            ? new MessageCreateParams
            {
                Model = config.Model,
                MaxTokens = config.MaxTokens,
                System = systemPrompt,
                Messages = messages
            }
            : new MessageCreateParams
            {
                Model = config.Model,
                MaxTokens = config.MaxTokens,
                System = systemPrompt,
                Messages = messages,
                Tools = tools.ToList()
            };
    }

    private async IAsyncEnumerable<AssistantStreamItem> AskOpenAiAsync(
        AssistantRuntimeConfig config,
        string systemPrompt,
        string userMessage,
        IReadOnlyList<AssistantMessage> history,
        IReadOnlyCollection<IAssistantTool> allowedTools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var apiKey = await providerSettings.GetDecryptedApiKeyAsync(config.Provider, cancellationToken)
            ?? throw new InvalidOperationException($"{config.Provider} API key is not configured.");

        var settings = await providerSettings.GetAsync(config.Provider, cancellationToken);
        var endpointUrl = settings?.EndpointUrl;

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(endpointUrl) && Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpointUri))
        {
            clientOptions.Endpoint = endpointUri;
        }

        var chatClient = new ChatClient(config.Model, new ApiKeyCredential(apiKey), clientOptions);

        var openAiTools = allowedTools.Count > 0 ? OpenAiToolMapper.Map(allowedTools) : null;

        var chatMessages = new List<OpenAI.Chat.ChatMessage>(history.Count + 2);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            chatMessages.Add(new SystemChatMessage(systemPrompt));
        }
        foreach (var msg in history)
        {
            OpenAI.Chat.ChatMessage built = msg.Role switch
            {
                AssistantMessageRole.System => new SystemChatMessage(msg.Content),
                AssistantMessageRole.Assistant => new AssistantChatMessage(msg.Content),
                _ => new UserChatMessage(msg.Content)
            };
            chatMessages.Add(built);
        }
        chatMessages.Add(new UserChatMessage(userMessage));

        string responseModel = config.Model;
        for (var turn = 0; turn < config.MaxTurns; turn++)
        {
            var completionOptions = new ChatCompletionOptions { MaxOutputTokenCount = config.MaxTokens };
            if (openAiTools is not null)
            {
                foreach (var tool in openAiTools) completionOptions.Tools.Add(tool);
            }

            var assistantTextThisTurn = new StringBuilder();
            var pendingToolCalls = new Dictionary<int, PendingOpenAiToolCall>();
            ChatTokenUsage? finalUsage = null;
            ChatFinishReason? finishReason = null;

            await foreach (var update in chatClient.CompleteChatStreamingAsync(chatMessages, completionOptions, cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(update.Model))
                {
                    responseModel = update.Model;
                }

                foreach (var part in update.ContentUpdate)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        assistantTextThisTurn.Append(part.Text);
                        yield return new AssistantTextDelta(part.Text);
                    }
                }

                foreach (var tcUpdate in update.ToolCallUpdates)
                {
                    if (!pendingToolCalls.TryGetValue(tcUpdate.Index, out var pending))
                    {
                        pending = new PendingOpenAiToolCall(tcUpdate.ToolCallId, tcUpdate.FunctionName, new StringBuilder());
                        pendingToolCalls[tcUpdate.Index] = pending;
                    }
                    else
                    {
                        // The id and function name only arrive on the first chunk in OpenAI's
                        // streaming protocol; later chunks carry only the arguments delta.
                        if (string.IsNullOrEmpty(pending.Id) && !string.IsNullOrEmpty(tcUpdate.ToolCallId))
                        {
                            pending = pending with { Id = tcUpdate.ToolCallId };
                            pendingToolCalls[tcUpdate.Index] = pending;
                        }
                        if (string.IsNullOrEmpty(pending.Name) && !string.IsNullOrEmpty(tcUpdate.FunctionName))
                        {
                            pending = pending with { Name = tcUpdate.FunctionName };
                            pendingToolCalls[tcUpdate.Index] = pending;
                        }
                    }

                    if (tcUpdate.FunctionArgumentsUpdate is { } argsUpdate)
                    {
                        pending.JsonBuffer.Append(argsUpdate.ToString());
                    }
                }

                if (update.FinishReason.HasValue)
                {
                    finishReason = update.FinishReason;
                }

                if (update.Usage is not null)
                {
                    finalUsage = update.Usage;
                }
            }

            if (pendingToolCalls.Count == 0)
            {
                if (SerializeOpenAiUsage(finalUsage) is { } usageJson)
                {
                    yield return new AssistantTokenUsage(config.Provider, responseModel, usageJson);
                }
                yield return new AssistantTurnDone(config.Provider, responseModel);
                yield break;
            }

            if (SerializeOpenAiUsage(finalUsage) is { } turnUsageJson)
            {
                yield return new AssistantTokenUsage(config.Provider, responseModel, turnUsageJson);
            }

            // Build a single AssistantChatMessage that carries (a) any text the model emitted
            // before the tool calls and (b) the tool_calls themselves. OpenAI requires the tool
            // result messages to come AFTER an assistant message that announces those tool calls.
            var toolCalls = pendingToolCalls
                .OrderBy(kvp => kvp.Key)
                .Select(kvp =>
                {
                    var p = kvp.Value;
                    return ChatToolCall.CreateFunctionToolCall(
                        p.Id ?? throw new InvalidOperationException("OpenAI tool call missing id."),
                        p.Name ?? throw new InvalidOperationException("OpenAI tool call missing function name."),
                        BinaryData.FromBytes(Encoding.UTF8.GetBytes(p.JsonBuffer.ToString())));
                })
                .ToArray();

            var assistantTurn = new AssistantChatMessage(toolCalls);
            if (assistantTextThisTurn.Length > 0)
            {
                assistantTurn.Content.Add(ChatMessageContentPart.CreateTextPart(assistantTextThisTurn.ToString()));
            }
            chatMessages.Add(assistantTurn);

            foreach (var (_, pending) in pendingToolCalls.OrderBy(kvp => kvp.Key))
            {
                var args = ParseToolArguments(pending.JsonBuffer.ToString());
                var id = pending.Id ?? string.Empty;
                var name = pending.Name ?? string.Empty;
                yield return new AssistantToolCallStarted(id, name, args);
                var result = await toolDispatcher.InvokeAsync(name, args, cancellationToken);
                yield return new AssistantToolCallCompleted(id, name, result.ResultJson, result.IsError);

                chatMessages.Add(new ToolChatMessage(id, result.ResultJson));
            }

            _ = finishReason; // available for future logging
        }

        logger.LogWarning(
            "OpenAI assistant turn hit MaxTurns={MaxTurns} without producing a final answer.",
            config.MaxTurns);
        yield return new AssistantTurnError(
            $"Assistant exceeded the {config.MaxTurns}-turn tool-loop budget without producing a final answer.");
    }

    private static Dictionary<string, JsonElement> ParseToolInputAsDictionary(string json)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return dict;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return dict;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }
        }
        catch (JsonException)
        {
            // Leave empty — the dispatcher will surface a tool error to the model.
        }
        return dict;
    }

    private static JsonElement ParseToolArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    private sealed record PendingAnthropicToolUse(string Id, string Name, StringBuilder JsonBuffer);
    private sealed record PendingOpenAiToolCall(string? Id, string? Name, StringBuilder JsonBuffer);

    private static JsonElement? SerializeAnthropicUsage(MessageDeltaUsage? final)
    {
        if (final is null)
        {
            return null;
        }

        // Verbatim passthrough — TokenUsageRecord stores raw provider fields so future provider
        // additions (cache_creation_input_tokens, etc.) land without a code change here.
        var combined = new Dictionary<string, object?>
        {
            ["input_tokens"] = final.InputTokens,
            ["output_tokens"] = final.OutputTokens,
            ["cache_creation_input_tokens"] = final.CacheCreationInputTokens,
            ["cache_read_input_tokens"] = final.CacheReadInputTokens,
        };

        return JsonSerializer.SerializeToElement(combined);
    }

    private static JsonElement? SerializeOpenAiUsage(OpenAI.Chat.ChatTokenUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var combined = new Dictionary<string, object?>
        {
            ["prompt_tokens"] = usage.InputTokenCount,
            ["completion_tokens"] = usage.OutputTokenCount,
            ["total_tokens"] = usage.TotalTokenCount
        };

        return JsonSerializer.SerializeToElement(combined);
    }
}
