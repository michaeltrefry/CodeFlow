namespace CodeFlow.Api.Assistant;

public interface IAssistantSettingsResolver
{
    /// <summary>
    /// Resolve the assistant's runtime configuration. <paramref name="overrideProvider"/> and
    /// <paramref name="overrideModel"/> let a single conversation pin a non-default provider/model
    /// (HAA-16); when null the resolver falls back to <see cref="AssistantSettingsEntity"/> (the
    /// admin-configured defaults from HAA-15) and finally to <see cref="AssistantOptions"/>.
    /// </summary>
    Task<AssistantRuntimeConfig> ResolveAsync(
        string? overrideProvider = null,
        string? overrideModel = null,
        CancellationToken cancellationToken = default);
}
