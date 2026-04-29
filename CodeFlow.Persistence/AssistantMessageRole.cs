namespace CodeFlow.Persistence;

public enum AssistantMessageRole
{
    System = 0,
    User = 1,
    Assistant = 2,
    /// <summary>
    /// Auto-compaction synthesis. Persisted alongside ordinary turns so the UI can render a
    /// divider where the conversation was summarized, but treated specially when feeding the
    /// LLM: the most recent Summary message is hoisted into the system prompt and the messages
    /// it superseded (anything with <c>Sequence ≤ AssistantConversation.CompactedThroughSequence</c>)
    /// are dropped from the outgoing history.
    /// </summary>
    Summary = 3
}
