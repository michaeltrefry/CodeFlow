using System.Text.Json;

namespace CodeFlow.Runtime;

/// <param name="RawUsage">Provider-reported usage payload, verbatim. When non-null, the token-
/// usage capture observer in <c>CodeFlow.Orchestration</c> persists a <c>TokenUsageRecord</c>
/// using this element's raw text. Null means the model client did not surface raw usage for this
/// call — capture is skipped (avoids partial records). Must be a cloned/owned element so the
/// caller can safely retain it after the parsed <c>JsonDocument</c> is disposed.</param>
public sealed record InvocationResponse(
    ChatMessage Message,
    InvocationStopReason StopReason,
    TokenUsage? TokenUsage = null,
    JsonElement? RawUsage = null);
