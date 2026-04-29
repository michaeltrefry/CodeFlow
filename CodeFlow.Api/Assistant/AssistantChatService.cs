using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using MassTransit;
using Microsoft.Extensions.Logging;
using TokenUsageRecorded = CodeFlow.Contracts.TokenUsageRecorded;

namespace CodeFlow.Api.Assistant;

/// <summary>
/// Single-turn orchestrator. Persists the user message, drives <see cref="CodeFlowAssistant"/>
/// for the streaming reply, captures token usage against the conversation's synthetic trace, and
/// persists the assistant message after the stream completes. The HTTP endpoint consumes the
/// emitted <see cref="AssistantTurnEvent"/> stream and translates it into SSE.
/// </summary>
public sealed class AssistantChatService(
    IAssistantConversationRepository conversations,
    IAssistantSettingsResolver settingsResolver,
    ICodeFlowAssistant assistant,
    ITokenUsageRecordRepository tokenUsageRepository,
    IPublishEndpoint publishEndpoint,
    IAssistantUserResolver userResolver,
    IAssistantConversationCompactor compactor,
    ILogger<AssistantChatService> logger)
{
    public async IAsyncEnumerable<AssistantTurnEvent> SendMessageAsync(
        Guid conversationId,
        string userContent,
        AssistantPageContext? pageContext = null,
        string? overrideProvider = null,
        string? overrideModel = null,
        AssistantWorkspaceTarget? workspaceOverride = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userContent);

        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken)
            ?? throw new InvalidOperationException($"Assistant conversation '{conversationId}' does not exist.");

        // HAA-15 — Enforce the per-conversation cumulative-token cap before persisting the user
        // message so the user gets a clear refusal instead of a half-stored prompt and a silent
        // dead-end. We deliberately swallow resolver failures here — the cap is a soft guardrail,
        // and a real configuration error will surface in <see cref="ICodeFlowAssistant.AskAsync"/>
        // below with the actual provider name in the message.
        var cap = await TryReadConversationCapAsync(overrideProvider, overrideModel, cancellationToken);

        // Auto-compaction: at 95% of the cap, summarize history and reset cumulative totals so
        // the next turn has a fresh budget. If compaction succeeds we re-fetch the conversation
        // (totals are now zero) and continue normally. If it fails we fall through to the hard
        // cap check below — the user will still get a clear refusal rather than a half-broken
        // turn.
        if (compactor.ShouldCompact(conversation, cap))
        {
            AssistantCompactionResult? compaction = null;
            try
            {
                compaction = await compactor.CompactAsync(conversation, overrideProvider, overrideModel, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Auto-compaction failed for conversation {ConversationId}; user will see the hard-cap refusal.",
                    conversationId);
            }

            if (compaction is not null)
            {
                yield return new ConversationCompacted(
                    compaction.SummaryMessage,
                    compaction.CompactedThroughSequence,
                    compaction.InputTokensTotal,
                    compaction.OutputTokensTotal);

                conversation = await conversations.GetByIdAsync(conversationId, cancellationToken)
                    ?? throw new InvalidOperationException($"Assistant conversation '{conversationId}' disappeared mid-turn.");
            }
        }

        if (cap is { } capValue
            && conversation.InputTokensTotal + conversation.OutputTokensTotal >= capValue)
        {
            yield return new TurnFailed(
                $"This conversation has hit the {capValue:N0}-token limit. Start a new conversation to continue.");
            yield break;
        }

        var userMessage = await conversations.AppendMessageAsync(
            conversationId,
            AssistantMessageRole.User,
            userContent.Trim(),
            provider: null,
            model: null,
            invocationId: null,
            cancellationToken);

        yield return new UserMessagePersisted(userMessage);

        var history = await conversations.ListMessagesForLlmAsync(conversationId, cancellationToken);
        // Exclude the just-appended user message from history; assistant.AskAsync re-adds it as
        // the current turn's prompt. The repository already filtered messages below the
        // compaction watermark.
        var historyForLlm = history
            .Where(m => m.Id != userMessage.Id)
            .OrderBy(m => m.Sequence)
            .ToArray();

        var invocationId = Guid.NewGuid();
        var contentBuffer = new StringBuilder();
        string? finalProvider = null;
        string? finalModel = null;
        // Track cumulative totals locally so the frontend gets a live tally without reading back
        // from the DB. Seeded with the persisted totals already on the conversation entity.
        var inputTotal = conversation.InputTokensTotal;
        var outputTotal = conversation.OutputTokensTotal;
        // HAA-6 demo mode: anonymous homepage conversations get no tool access — system-prompt
        // knowledge only. The conversation's UserId carries the marker (anon: prefix); the
        // resolver gives us a single source of truth.
        var toolPolicy = userResolver.IsDemoUser(conversation.UserId) ? ToolAccessPolicy.NoTools : null;
        var enumerator = assistant.AskAsync(
                userContent,
                historyForLlm,
                toolPolicy,
                pageContext,
                overrideProvider,
                overrideModel,
                conversationId,
                workspaceOverride,
                cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                AssistantStreamItem? item;
                string? failureMessage = null;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }
                    item = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Assistant turn failed for conversation {ConversationId}", conversationId);
                    failureMessage = ex.Message;
                    item = null;
                }

                if (failureMessage is not null)
                {
                    yield return new TurnFailed(failureMessage);
                    yield break;
                }

                switch (item)
                {
                    case AssistantTextDelta delta:
                        contentBuffer.Append(delta.Delta);
                        yield return new TextDelta(delta.Delta);
                        break;

                    case AssistantTokenUsage usage:
                        finalProvider = usage.Provider;
                        finalModel = usage.Model;
                        var record = new TokenUsageRecord(
                            Id: Guid.NewGuid(),
                            TraceId: conversation.SyntheticTraceId,
                            NodeId: conversation.Id,
                            InvocationId: invocationId,
                            ScopeChain: Array.Empty<Guid>(),
                            Provider: usage.Provider,
                            Model: usage.Model,
                            RecordedAtUtc: DateTime.UtcNow,
                            Usage: usage.Usage);
                        await tokenUsageRepository.AddAsync(record, cancellationToken);
                        await publishEndpoint.Publish(
                            new TokenUsageRecorded(
                                TraceId: record.TraceId,
                                RecordId: record.Id,
                                NodeId: record.NodeId,
                                InvocationId: record.InvocationId,
                                ScopeChain: record.ScopeChain,
                                Provider: record.Provider,
                                Model: record.Model,
                                RecordedAtUtc: record.RecordedAtUtc,
                                Usage: record.Usage),
                            cancellationToken);

                        // HAA-17 — extract input/output deltas from the provider-shape payload and
                        // persist a running total on the conversation entity so the live chip + the
                        // cap enforcement above don't have to re-aggregate every turn.
                        var (inputDelta, outputDelta) = ExtractInputOutputTokens(usage.Usage);
                        var totals = await conversations.AddTokenUsageAsync(
                            conversationId, inputDelta, outputDelta, cancellationToken);
                        inputTotal = totals.InputTokensTotal;
                        outputTotal = totals.OutputTokensTotal;

                        yield return new TokenUsageEmitted(record, inputTotal, outputTotal);
                        break;

                    case AssistantTurnDone done:
                        finalProvider ??= done.Provider;
                        finalModel ??= done.Model;
                        break;

                    case AssistantToolCallStarted tcs:
                        yield return new ToolCallStarted(tcs.ToolUseId, tcs.Name, tcs.Arguments);
                        break;

                    case AssistantToolCallCompleted tcc:
                        yield return new ToolCallCompleted(tcc.ToolUseId, tcc.Name, tcc.ResultJson, tcc.IsError);
                        break;

                    case AssistantTurnError err:
                        yield return new TurnFailed(err.Message);
                        yield break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        var assistantMessage = await conversations.AppendMessageAsync(
            conversationId,
            AssistantMessageRole.Assistant,
            contentBuffer.ToString(),
            provider: finalProvider,
            model: finalModel,
            invocationId: invocationId,
            cancellationToken);

        yield return new AssistantMessagePersisted(assistantMessage);
    }

    /// <summary>
    /// HAA-15 — best-effort lookup of the admin-configured cap. Swallows resolver failures so a
    /// missing provider configuration doesn't block the chat service from delegating to a
    /// (possibly stubbed) assistant.
    /// </summary>
    private async Task<long?> TryReadConversationCapAsync(
        string? overrideProvider, string? overrideModel, CancellationToken cancellationToken)
    {
        try
        {
            var config = await settingsResolver.ResolveAsync(overrideProvider, overrideModel, cancellationToken);
            return config.MaxTokensPerConversation;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls the (input, output) integer pair out of a provider-shape usage payload. Anthropic
    /// uses <c>input_tokens</c>/<c>output_tokens</c>; OpenAI uses <c>prompt_tokens</c>/
    /// <c>completion_tokens</c>. Anything missing falls back to 0 — the conversation total just
    /// doesn't move for that turn rather than throwing.
    /// </summary>
    private static (long Input, long Output) ExtractInputOutputTokens(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
        {
            return (0, 0);
        }

        var input = TryReadLong(usage, "input_tokens") ?? TryReadLong(usage, "prompt_tokens") ?? 0;
        var output = TryReadLong(usage, "output_tokens") ?? TryReadLong(usage, "completion_tokens") ?? 0;
        return (input, output);
    }

    private static long? TryReadLong(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop)) return null;
        if (prop.ValueKind != JsonValueKind.Number) return null;
        return prop.TryGetInt64(out var v) ? v : null;
    }
}

public abstract record AssistantTurnEvent;

public sealed record UserMessagePersisted(AssistantMessage Message) : AssistantTurnEvent;

public sealed record TextDelta(string Delta) : AssistantTurnEvent;

/// <summary>
/// HAA-17 — emitted after a per-turn token-usage record is persisted. Carries the raw record
/// (provider-shape <c>Usage</c>) plus the conversation's running totals so the live chip can
/// update without re-aggregating.
/// </summary>
public sealed record TokenUsageEmitted(
    TokenUsageRecord Record,
    long ConversationInputTokensTotal,
    long ConversationOutputTokensTotal) : AssistantTurnEvent;

public sealed record AssistantMessagePersisted(AssistantMessage Message) : AssistantTurnEvent;

/// <summary>
/// Emitted when auto-compaction has just synthesized and persisted a summary message in place
/// of older history. The frontend renders a divider tied to <see cref="Summary"/> and resets
/// its running token tally to <see cref="ResetInputTokensTotal"/> /
/// <see cref="ResetOutputTokensTotal"/>. Always precedes <see cref="UserMessagePersisted"/>
/// for the current turn.
/// </summary>
public sealed record ConversationCompacted(
    AssistantMessage Summary,
    int CompactedThroughSequence,
    long ResetInputTokensTotal,
    long ResetOutputTokensTotal) : AssistantTurnEvent;

public sealed record TurnFailed(string Message) : AssistantTurnEvent;

/// <summary>
/// A tool call requested by the model. Emitted before the tool runs so the UI can show a
/// "running" state. <see cref="Arguments"/> is the parsed JSON the model produced as the input.
/// </summary>
public sealed record ToolCallStarted(string Id, string Name, JsonElement Arguments) : AssistantTurnEvent;

/// <summary>
/// The result of a tool call. <see cref="ResultJson"/> is the raw JSON string returned by the
/// tool (preserved unparsed so the UI can render it verbatim). <see cref="IsError"/> distinguishes
/// a recoverable tool error from a successful invocation.
/// </summary>
public sealed record ToolCallCompleted(string Id, string Name, string ResultJson, bool IsError) : AssistantTurnEvent;
