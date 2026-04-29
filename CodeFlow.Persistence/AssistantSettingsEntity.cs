namespace CodeFlow.Persistence;

/// <summary>
/// HAA-15 — DB-backed defaults for the homepage AI assistant. The table holds a single row
/// (<see cref="SingletonKey"/>) that selects which configured LLM provider/model the assistant
/// uses on a fresh conversation and caps cumulative tokens per conversation. The
/// per-provider api keys / endpoints / model lists still live in
/// <see cref="LlmProviderSettingsEntity"/>; this entity only carries the assistant-specific
/// "which of those providers do I default to" selection plus the conversation-level token cap.
/// </summary>
public sealed class AssistantSettingsEntity
{
    public const string SingletonKey = "default";

    /// <summary>Always <see cref="SingletonKey"/>. The table is single-row by design.</summary>
    public string Key { get; set; } = SingletonKey;

    /// <summary>Canonical provider key (anthropic / openai / lmstudio). Null falls back to options.</summary>
    public string? Provider { get; set; }

    /// <summary>Specific model id within the provider. Null falls back to options / first listed.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Cap on the cumulative input+output tokens captured against a single conversation. Zero or
    /// null means "no cap". Enforced in <c>AssistantChatService.SendMessageAsync</c> before the
    /// next turn runs.
    /// </summary>
    public long? MaxTokensPerConversation { get; set; }

    /// <summary>
    /// Optional agent role whose tool grants are merged into the homepage assistant's tool surface
    /// alongside the built-in <c>IAssistantTool</c> registry. Null means "built-in tools only".
    /// FK to <see cref="AgentRoleEntity.Id"/>; ON DELETE SET NULL so deleting a role detaches it
    /// from the assistant rather than wiping the settings row.
    /// </summary>
    public long? AssignedAgentRoleId { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
