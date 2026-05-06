using System.Text.Json;
using CodeFlow.Persistence;

namespace CodeFlow.Api.Assistant;

/// <summary>
/// sc-808 (AR-6) — Single source of truth for translating an <see cref="AssistantTurnEvent"/>
/// (the chat service's strongly-typed stream) into the SSE-frame pair (event name, JSON
/// payload) the wire format uses. Used by both the originating endpoint and the new
/// <see cref="IAssistantTurnTaskRegistry"/> background producer so the persisted recorder
/// stream + the live SSE stream + replays all share the same shape.
/// </summary>
internal static class AssistantTurnFrameMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static (string EventName, string Payload) Map(AssistantTurnEvent evt) => evt switch
    {
        UserMessagePersisted m => ("user-message-persisted", JsonSerializer.Serialize(MapMessage(m.Message), JsonOptions)),
        TextDelta t => ("text-delta", JsonSerializer.Serialize(new { delta = t.Delta }, JsonOptions)),
        TokenUsageEmitted u => ("token-usage", JsonSerializer.Serialize(new
        {
            recordId = u.Record.Id,
            provider = u.Record.Provider,
            model = u.Record.Model,
            usage = u.Record.Usage,
            conversationInputTokensTotal = u.ConversationInputTokensTotal,
            conversationOutputTokensTotal = u.ConversationOutputTokensTotal,
        }, JsonOptions)),
        AssistantMessagePersisted m => ("assistant-message-persisted", JsonSerializer.Serialize(MapMessage(m.Message), JsonOptions)),
        ToolCallStarted tcs => ("tool-call", JsonSerializer.Serialize(new
        {
            id = tcs.Id,
            name = tcs.Name,
            arguments = tcs.Arguments,
        }, JsonOptions)),
        ToolCallCompleted tcc => ("tool-result", JsonSerializer.Serialize(new
        {
            id = tcc.Id,
            name = tcc.Name,
            result = tcc.ResultJson,
            isError = tcc.IsError,
        }, JsonOptions)),
        ArtifactEventEmitted ae => ("artifact-event", JsonSerializer.Serialize(new
        {
            id = ae.Event.Id,
            conversationId = ae.Event.ConversationId,
            sequence = ae.Event.Sequence,
            kind = ae.Event.Kind.ToString(),
            name = ae.Event.Name,
            snapshotId = ae.Event.SnapshotId,
            summary = ae.Event.SummaryJson,
            supersedesPriorByName = ae.SupersedesPriorByName,
            createdAtUtc = ae.Event.CreatedAtUtc,
        }, JsonOptions)),
        TurnFailed f => ("error", JsonSerializer.Serialize(new { message = f.Message }, JsonOptions)),
        _ => ("unknown", "{}"),
    };

    /// <summary>
    /// sc-808 (AR-6) — Synthetic <c>error</c> frame emitted by the registry when the producer
    /// throws an exception that the chat service didn't already render as a
    /// <see cref="TurnFailed"/>. Same shape as the in-band <c>TurnFailed</c> mapping so a
    /// retry replaying the recorded events sees a single uniform error frame regardless of
    /// whether the failure was structured or thrown.
    /// </summary>
    public static (string EventName, string Payload) BuildProducerFaultFrame(string message) =>
        ("error", JsonSerializer.Serialize(new { message }, JsonOptions));

    private static object MapMessage(AssistantMessage message) => new
    {
        id = message.Id,
        sequence = message.Sequence,
        role = message.Role.ToString().ToLowerInvariant(),
        content = message.Content,
        provider = message.Provider,
        model = message.Model,
        invocationId = message.InvocationId,
        createdAtUtc = message.CreatedAtUtc,
    };
}
