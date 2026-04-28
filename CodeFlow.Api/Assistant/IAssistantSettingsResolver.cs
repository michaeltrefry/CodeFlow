namespace CodeFlow.Api.Assistant;

public interface IAssistantSettingsResolver
{
    Task<AssistantRuntimeConfig> ResolveAsync(CancellationToken cancellationToken = default);
}
