using System.Text.Json;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Provider-agnostic contract for a single assistant tool. Each tool advertises a stable name,
/// human description (LLM-facing), and a JSON Schema describing its input. <see cref="CodeFlowAssistant"/>
/// translates the registry of tools into the configured provider's tool-calling shape (Anthropic
/// <c>tool_use</c> blocks or OpenAI <c>tools</c> entries).
/// </summary>
/// <remarks>
/// HAA-4 ships read-only registry tools (<c>list_workflows</c>, <c>get_agent</c>, etc.). HAA-5 adds
/// trace tools and HAA-9..12 add mutating tools. Mutating tools live behind in-chat confirmation
/// chips per the epic defaults — the dispatcher itself does not gate; the chat UI does.
/// </remarks>
public interface IAssistantTool
{
    /// <summary>
    /// Stable identifier the LLM uses to call this tool. Snake_case by convention (matches the
    /// CodeGraph assistant's tool naming and what the system prompt instructs the model to use).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Short LLM-facing description. The model decides whether to invoke a tool based on this
    /// string + the input schema, so write it for the model not for humans.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema (draft-07 compatible — both providers accept this) describing the tool's
    /// input object. Keep schemas tight: required fields named, types narrow, defaults declared
    /// in the description rather than via Schema <c>default</c> (provider compatibility varies).
    /// </summary>
    JsonElement InputSchema { get; }

    /// <summary>
    /// Invoke the tool. <paramref name="arguments"/> is whatever the LLM produced — the dispatcher
    /// has already verified it parsed as JSON but has not validated against the schema.
    /// Implementations should bound their result size; oversized results may be truncated by the
    /// dispatcher to protect the model's context window.
    /// </summary>
    Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>
/// The output of a tool invocation. <see cref="ResultJson"/> is whatever JSON the tool emits;
/// <see cref="IsError"/> distinguishes "tool ran fine and returned an error description" (false,
/// dispatcher only forwards the result) from "tool itself failed" (true, dispatcher tags the
/// content with the provider's error marker so the LLM treats it as a hard failure).
/// </summary>
public sealed record AssistantToolResult(string ResultJson, bool IsError = false);
