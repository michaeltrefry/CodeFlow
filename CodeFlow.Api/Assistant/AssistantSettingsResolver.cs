using CodeFlow.Persistence;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Assistant;

/// <summary>
/// Default resolver: takes <see cref="AssistantOptions"/> as the baseline, falls back to the first
/// model listed in <see cref="LlmProviderSettings"/> for the chosen provider when
/// <see cref="AssistantOptions.Model"/> is unset. Throws when the chosen provider has no API key
/// configured — surfacing the configuration gap to the caller is preferable to a silent default.
/// </summary>
public sealed class AssistantSettingsResolver(
    IOptions<AssistantOptions> optionsAccessor,
    ILlmProviderSettingsRepository providerSettings)
    : IAssistantSettingsResolver
{
    public async Task<AssistantRuntimeConfig> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var options = optionsAccessor.Value;
        var provider = string.IsNullOrWhiteSpace(options.Provider)
            ? LlmProviderKeys.Anthropic
            : LlmProviderKeys.Canonicalize(options.Provider.Trim());

        var settings = await providerSettings.GetAsync(provider, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Assistant provider '{provider}' is not configured. Add a row in the LLM providers admin first.");

        if (!settings.HasApiKey)
        {
            throw new InvalidOperationException(
                $"Assistant provider '{provider}' has no API key configured.");
        }

        var model = !string.IsNullOrWhiteSpace(options.Model)
            ? options.Model.Trim()
            : settings.Models.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"Assistant provider '{provider}' has no models listed and no default Model set in AssistantOptions.");

        return new AssistantRuntimeConfig(
            Provider: provider,
            Model: model,
            MaxTokens: options.MaxTokens > 0 ? options.MaxTokens : 4096,
            MaxTurns: options.MaxTurns > 0 ? options.MaxTurns : 10);
    }
}
