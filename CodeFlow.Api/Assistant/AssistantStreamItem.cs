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

/// <summary>
/// Emitted when the LLM requests a tool call. <see cref="ToolUseId"/> is the provider's id (used
/// later to correlate the result). <see cref="Arguments"/> is the parsed JSON object the model
/// produced as the tool input. The assistant emits this BEFORE running the tool so the UI can
/// render a pending state.
/// </summary>
public sealed record AssistantToolCallStarted(string ToolUseId, string Name, JsonElement Arguments)
    : AssistantStreamItem;

/// <summary>
/// Emitted after a tool finishes. <see cref="IsError"/> distinguishes a recoverable tool error
/// (forwarded to the LLM as a tool error) from a successful invocation. <see cref="ResultJson"/>
/// is the raw JSON string the tool produced — preserved unparsed so the UI can render it
/// faithfully without a re-serialize round-trip.
/// </summary>
public sealed record AssistantToolCallCompleted(string ToolUseId, string Name, string ResultJson, bool IsError)
    : AssistantStreamItem;
