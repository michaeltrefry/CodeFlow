using System.Text.Json;

namespace CodeFlow.Api.Assistant;

/// <summary>
/// Discriminated-union-ish stream item emitted by <see cref="CodeFlowAssistant"/>. The HTTP
/// endpoint translates each variant into a typed SSE event.
/// </summary>
public abstract record AssistantStreamItem;

public sealed record AssistantTextDelta(string Delta) : AssistantStreamItem;

public sealed record AssistantTokenUsage(string Provider, string Model, JsonElement Usage)
    : AssistantStreamItem;

public sealed record AssistantTurnDone(string Provider, string Model) : AssistantStreamItem;

public sealed record AssistantTurnError(string Message) : AssistantStreamItem;
