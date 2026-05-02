using CodeFlow.Api.Assistant.Skills;

namespace CodeFlow.Api.Assistant;

/// <summary>
/// Resolves the system prompt for an assistant turn. The default implementation composes the
/// curated base prompt with a dynamically rendered skill catalog so newly added skills surface
/// to the model without a code change to the prompt itself.
/// </summary>
public interface IAssistantSystemPromptProvider
{
    Task<string> GetSystemPromptAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Composes the system prompt by reading the registered <see cref="IAssistantSkillProvider"/> at
/// turn time and substituting its catalog into the base prompt's placeholder. Singleton-friendly.
/// </summary>
public sealed class DefaultAssistantSystemPromptProvider(IAssistantSkillProvider skills)
    : IAssistantSystemPromptProvider
{
    public Task<string> GetSystemPromptAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(AssistantSystemPrompt.Compose(skills.List()));
}
