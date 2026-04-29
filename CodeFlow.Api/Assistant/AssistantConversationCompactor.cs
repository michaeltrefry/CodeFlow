using System.ClientModel;
using System.Text;
using Anthropic;
using Anthropic.Models.Messages;
using CodeFlow.Persistence;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace CodeFlow.Api.Assistant;

public interface IAssistantConversationCompactor
{
    /// <summary>
    /// True when the conversation has tripped the auto-compaction threshold (95% of the
    /// configured per-conversation cap). False when there is no cap, no usage yet, or the cap
    /// is disabled. Cheap — reads only the materialized totals on the conversation entity.
    /// </summary>
    bool ShouldCompact(AssistantConversation conversation, long? maxTokensPerConversation);

    /// <summary>
    /// Generate a summary of the existing conversation history via the configured LLM, persist it
    /// as a <see cref="AssistantMessageRole.Summary"/> message, advance the compaction watermark,
    /// and reset cumulative token totals. Returns null if there is nothing meaningful to compact
    /// (e.g. fewer than two messages above the existing watermark).
    /// </summary>
    Task<AssistantCompactionResult?> CompactAsync(
        AssistantConversation conversation,
        string? overrideProvider,
        string? overrideModel,
        CancellationToken cancellationToken);
}

/// <summary>
/// Token-budget rescue valve. Triggered by <see cref="AssistantChatService"/> right before a
/// turn that would otherwise refuse with a "conversation full" error: summarizes everything
/// above the previous compaction watermark down to a few hundred tokens, persists the synthesis
/// as a <see cref="AssistantMessageRole.Summary"/> message, and resets cumulative token totals
/// so the next turn has a fresh budget. The summary is hoisted into the system prompt by
/// <see cref="CodeFlowAssistant"/> so the model still has context after compaction.
/// </summary>
public sealed class AssistantConversationCompactor(
    IAssistantConversationRepository conversations,
    IAssistantSettingsResolver settingsResolver,
    ILlmProviderSettingsRepository providerSettings,
    IAnthropicClient anthropicClient,
    ILogger<AssistantConversationCompactor> logger) : IAssistantConversationCompactor
{
    public const double CompactionThresholdRatio = 0.95;

    private const int MaxSummaryOutputTokens = 1024;

    public bool ShouldCompact(AssistantConversation conversation, long? maxTokensPerConversation)
    {
        if (maxTokensPerConversation is not { } cap || cap <= 0)
        {
            return false;
        }

        var used = conversation.InputTokensTotal + conversation.OutputTokensTotal;
        return used >= (long)Math.Ceiling(cap * CompactionThresholdRatio);
    }

    public async Task<AssistantCompactionResult?> CompactAsync(
        AssistantConversation conversation,
        string? overrideProvider,
        string? overrideModel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var history = await conversations.ListMessagesForLlmAsync(conversation.Id, cancellationToken);
        // Need at least one user/assistant exchange to make a summary worth the round trip; if the
        // conversation only has the prior summary above the watermark, skip — a second summary of
        // a summary would just rephrase the same content.
        var contentful = history.Count(m => m.Role is AssistantMessageRole.User or AssistantMessageRole.Assistant);
        if (contentful < 2)
        {
            logger.LogInformation(
                "Skipping compaction for conversation {ConversationId}: only {Count} non-summary messages above the watermark.",
                conversation.Id, contentful);
            return null;
        }

        var config = await settingsResolver.ResolveAsync(overrideProvider, overrideModel, cancellationToken);
        var transcript = BuildTranscript(history);

        var summary = config.Provider switch
        {
            LlmProviderKeys.Anthropic => await SummarizeAnthropicAsync(config, transcript, cancellationToken),
            LlmProviderKeys.OpenAi or LlmProviderKeys.LmStudio => await SummarizeOpenAiAsync(config, transcript, cancellationToken),
            _ => throw new InvalidOperationException(
                $"Assistant provider '{config.Provider}' is not supported for compaction.")
        };

        if (string.IsNullOrWhiteSpace(summary))
        {
            logger.LogWarning(
                "Compaction summary for conversation {ConversationId} came back empty; leaving the conversation unmodified.",
                conversation.Id);
            return null;
        }

        var result = await conversations.CompactAsync(
            conversation.Id,
            summary.Trim(),
            config.Provider,
            config.Model,
            cancellationToken);

        logger.LogInformation(
            "Compacted assistant conversation {ConversationId}: watermark advanced to {Watermark}, totals reset.",
            conversation.Id, result.CompactedThroughSequence);

        return result;
    }

    private static string BuildTranscript(IReadOnlyList<AssistantMessage> history)
    {
        var sb = new StringBuilder();
        foreach (var msg in history)
        {
            var label = msg.Role switch
            {
                AssistantMessageRole.User => "User",
                AssistantMessageRole.Assistant => "Assistant",
                AssistantMessageRole.Summary => "Prior summary",
                AssistantMessageRole.System => "System",
                _ => msg.Role.ToString()
            };
            sb.Append(label).Append(": ").AppendLine(msg.Content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private const string SummarySystemPrompt =
        "You are compacting an in-progress chat between a CodeFlow user and the CodeFlow assistant " +
        "so the conversation can continue past its token budget. Produce a faithful, factual " +
        "summary in 200–400 words covering: the user's goals and any open questions, decisions " +
        "made, identifiers and file paths referenced, tool results that affect future turns, and " +
        "any unresolved errors. Preserve concrete identifiers (IDs, paths, names) verbatim. " +
        "Output only the summary — no preamble.";

    private async Task<string?> SummarizeAnthropicAsync(
        AssistantRuntimeConfig config,
        string transcript,
        CancellationToken cancellationToken)
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

        var createParams = new MessageCreateParams
        {
            Model = config.Model,
            MaxTokens = MaxSummaryOutputTokens,
            System = SummarySystemPrompt,
            Messages = new List<MessageParam>
            {
                new() { Role = Role.User, Content = "Conversation transcript to summarize:\n\n" + transcript }
            }
        };

        var response = await client.Messages.Create(createParams, cancellationToken);
        var sb = new StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text))
            {
                sb.Append(text.Text);
            }
        }
        return sb.ToString();
    }

    private async Task<string?> SummarizeOpenAiAsync(
        AssistantRuntimeConfig config,
        string transcript,
        CancellationToken cancellationToken)
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

        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new SystemChatMessage(SummarySystemPrompt),
            new UserChatMessage("Conversation transcript to summarize:\n\n" + transcript)
        };

        var completionOptions = new ChatCompletionOptions { MaxOutputTokenCount = MaxSummaryOutputTokens };
        var completion = await chatClient.CompleteChatAsync(messages, completionOptions, cancellationToken);
        var sb = new StringBuilder();
        foreach (var part in completion.Value.Content)
        {
            if (!string.IsNullOrEmpty(part.Text))
            {
                sb.Append(part.Text);
            }
        }
        return sb.ToString();
    }
}
