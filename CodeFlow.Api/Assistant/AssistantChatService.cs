using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeFlow.Persistence;
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
    ICodeFlowAssistant assistant,
    ITokenUsageRecordRepository tokenUsageRepository,
    IPublishEndpoint publishEndpoint,
    ILogger<AssistantChatService> logger)
{
    public async IAsyncEnumerable<AssistantTurnEvent> SendMessageAsync(
        Guid conversationId,
        string userContent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userContent);

        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken)
            ?? throw new InvalidOperationException($"Assistant conversation '{conversationId}' does not exist.");

        var userMessage = await conversations.AppendMessageAsync(
            conversationId,
            AssistantMessageRole.User,
            userContent.Trim(),
            provider: null,
            model: null,
            invocationId: null,
            cancellationToken);

        yield return new UserMessagePersisted(userMessage);

        var history = await conversations.ListMessagesAsync(conversationId, cancellationToken);
        // Exclude the just-appended user message from history; assistant.AskAsync re-adds it as
        // the current turn's prompt.
        var historyForLlm = history
            .Where(m => m.Id != userMessage.Id)
            .OrderBy(m => m.Sequence)
            .ToArray();

        var invocationId = Guid.NewGuid();
        var contentBuffer = new StringBuilder();
        string? finalProvider = null;
        string? finalModel = null;
        var enumerator = assistant.AskAsync(userContent, historyForLlm, cancellationToken)
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
                        yield return new TokenUsageEmitted(record);
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
}

public abstract record AssistantTurnEvent;

public sealed record UserMessagePersisted(AssistantMessage Message) : AssistantTurnEvent;

public sealed record TextDelta(string Delta) : AssistantTurnEvent;

public sealed record TokenUsageEmitted(TokenUsageRecord Record) : AssistantTurnEvent;

public sealed record AssistantMessagePersisted(AssistantMessage Message) : AssistantTurnEvent;

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
