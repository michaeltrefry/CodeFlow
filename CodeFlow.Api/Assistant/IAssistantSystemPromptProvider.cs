namespace CodeFlow.Api.Assistant;

/// <summary>
/// Resolves the system prompt for an assistant turn. The default implementation returns the
/// hard-coded curated prompt in <see cref="AssistantSystemPrompt.Default"/>; a future DB-backed
/// implementation will overlay an admin-edited override on top.
/// </summary>
public interface IAssistantSystemPromptProvider
{
    Task<string> GetSystemPromptAsync(CancellationToken cancellationToken = default);
}

public sealed class DefaultAssistantSystemPromptProvider : IAssistantSystemPromptProvider
{
    public Task<string> GetSystemPromptAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(AssistantSystemPrompt.Default);
}
