namespace CodeFlow.Api.Assistant;

/// <summary>
/// Per-turn workspace selection for the homepage assistant. The chat composer sends this on
/// every request so the assistant's host tools (read_file / apply_patch / run_command) operate
/// against either the conversation's own workspace or the workdir of a specific trace the user
/// is viewing. Selection is explicit — the backend never auto-switches based on page context.
/// </summary>
public enum AssistantWorkspaceKind
{
    /// <summary>Default: <c>{AssistantWorkspaceRoot}/{conversationId:N}</c>, created on demand.</summary>
    Conversation = 0,

    /// <summary>Trace workdir: <c>{WorkingDirectoryRoot}/{traceId:N}</c>; must already exist.</summary>
    Trace = 1,
}

/// <summary>
/// Per-turn override sent by the chat composer. <see cref="TraceId"/> is required when
/// <see cref="Kind"/> is <see cref="AssistantWorkspaceKind.Trace"/> and ignored otherwise.
/// </summary>
public sealed record AssistantWorkspaceTarget(
    AssistantWorkspaceKind Kind,
    Guid? TraceId = null);
