namespace CodeFlow.Api.Assistant;

/// <summary>
/// Appsettings-backed defaults for the assistant chat loop. The per-provider api keys / endpoints
/// / model lists live in the existing <c>LlmProviderSettings</c> admin (CodeGraph LLM Configuration
/// pattern reused here). This options block selects which of those configured providers the
/// assistant uses by default and tunes per-turn limits.
/// </summary>
/// <remarks>
/// HAA-1 reads from <c>IOptions&lt;AssistantOptions&gt;</c> only; a follow-up admin UI for
/// DB-overlay overrides ships alongside other admin surfaces. <see cref="Model"/> being empty
/// means "use the first model listed for the resolved provider in <c>LlmProviderSettings</c>".
/// </remarks>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    public string Provider { get; set; } = "anthropic";

    public string Model { get; set; } = string.Empty;

    public int MaxTokens { get; set; } = 32768;

    /// <summary>
    /// Maximum tool-loop turns per user message. Surfaced in the system prompt's
    /// <c>&lt;turn-budget&gt;</c> block so the model can pace itself; raise via
    /// <c>Assistant__MaxTurns</c> when tool-heavy flows (workflow drafting, multi-step diagnosis)
    /// keep hitting the cap.
    /// </summary>
    public int MaxTurns { get; set; } = 25;
}
