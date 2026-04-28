using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using CodeFlow.Persistence;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace CodeFlow.Api.Assistant;

public interface ICodeFlowAssistant
{
    IAsyncEnumerable<AssistantStreamItem> AskAsync(
        string userMessage,
        IReadOnlyList<AssistantMessage> history,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Streaming chat-loop service. Routes requests to the configured provider's official SDK
/// (Anthropic or OpenAI; LMStudio reuses the OpenAI client with a custom endpoint) so each
/// provider's native streaming surface is used directly. HAA-1 ships without tools — the loop
/// is a single LLM call per user message; HAA-4/5/9-onward will layer the tool-call loop on top.
/// </summary>
/// <remarks>
/// Mirrors the architecture of CodeGraph's <c>GraphAssistant</c> (provider routing, SDK-native
/// streaming, per-call key/endpoint resolution) so the two assistants stay parallel.
/// </remarks>
public sealed class CodeFlowAssistant(
    IAssistantSettingsResolver settingsResolver,
    ILlmProviderSettingsRepository providerSettings,
    IAnthropicClient anthropicClient,
    ILogger<CodeFlowAssistant> logger) : ICodeFlowAssistant
{
    public async IAsyncEnumerable<AssistantStreamItem> AskAsync(
        string userMessage,
        IReadOnlyList<AssistantMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(history);

        var config = await settingsResolver.ResolveAsync(cancellationToken);

        IAsyncEnumerable<AssistantStreamItem> stream = config.Provider switch
        {
            LlmProviderKeys.Anthropic => AskAnthropicAsync(config, userMessage, history, cancellationToken),
            LlmProviderKeys.OpenAi or LlmProviderKeys.LmStudio => AskOpenAiAsync(config, userMessage, history, cancellationToken),
            _ => throw new InvalidOperationException(
                $"Assistant provider '{config.Provider}' is not supported. Expected one of: anthropic, openai, lmstudio.")
        };

        await foreach (var item in stream.WithCancellation(cancellationToken))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<AssistantStreamItem> AskAnthropicAsync(
        AssistantRuntimeConfig config,
        string userMessage,
        IReadOnlyList<AssistantMessage> history,
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

        await foreach (var ev in client.Messages.CreateStreaming(new MessageCreateParams
        {
            Model = config.Model,
            MaxTokens = config.MaxTokens,
            Messages = messages
        }, cancellationToken))
        {
            if (ev.TryPickContentBlockDelta(out var cbDelta) && cbDelta.Delta.TryPickText(out var td))
            {
                yield return new AssistantTextDelta(td.Text);
            }
            else if (ev.TryPickDelta(out var msgDelta))
            {
                finalUsage = msgDelta.Usage;
            }
        }

        if (SerializeAnthropicUsage(finalUsage) is { } usageJson)
        {
            yield return new AssistantTokenUsage(LlmProviderKeys.Anthropic, config.Model, usageJson);
        }

        yield return new AssistantTurnDone(LlmProviderKeys.Anthropic, config.Model);
    }

    private async IAsyncEnumerable<AssistantStreamItem> AskOpenAiAsync(
        AssistantRuntimeConfig config,
        string userMessage,
        IReadOnlyList<AssistantMessage> history,
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

        var chatMessages = new List<OpenAI.Chat.ChatMessage>(history.Count + 1);
        foreach (var msg in history)
        {
            ChatMessage built = msg.Role switch
            {
                AssistantMessageRole.System => new SystemChatMessage(msg.Content),
                AssistantMessageRole.Assistant => new AssistantChatMessage(msg.Content),
                _ => new UserChatMessage(msg.Content)
            };
            chatMessages.Add(built);
        }
        chatMessages.Add(new UserChatMessage(userMessage));

        var completionOptions = new ChatCompletionOptions
        {
            MaxOutputTokenCount = config.MaxTokens
        };

        OpenAI.Chat.ChatTokenUsage? finalUsage = null;
        string responseModel = config.Model;

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
                    yield return new AssistantTextDelta(part.Text);
                }
            }

            if (update.Usage is not null)
            {
                finalUsage = update.Usage;
            }
        }

        if (SerializeOpenAiUsage(finalUsage) is { } usageJson)
        {
            yield return new AssistantTokenUsage(config.Provider, responseModel, usageJson);
        }

        yield return new AssistantTurnDone(config.Provider, responseModel);

        // Suppress logger warning to keep linters happy until tools wire here in HAA-4+.
        _ = logger;
    }

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
