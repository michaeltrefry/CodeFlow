using CodeFlow.Persistence;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Assistant;

/// <summary>
/// Three-layer resolver:
/// <list type="number">
///   <item>Per-call override (HAA-16) — the user picked a provider/model for this turn.</item>
///   <item>DB-backed admin defaults from <see cref="IAssistantSettingsRepository"/> (HAA-15).</item>
///   <item>Appsettings <see cref="AssistantOptions"/> baseline.</item>
/// </list>
/// At each layer a non-null/non-blank value wins. The chosen provider must have an api key
/// configured in <see cref="ILlmProviderSettingsRepository"/>; the chosen model must be either
/// supplied explicitly or be present in that provider's listed models. Configuration gaps throw
/// so callers see a clear error rather than a silent fallback to a wrong provider.
/// </summary>
public sealed class AssistantSettingsResolver(
    IOptions<AssistantOptions> optionsAccessor,
    IAssistantSettingsRepository assistantSettings,
    ILlmProviderSettingsRepository providerSettings)
    : IAssistantSettingsResolver
{
    public async Task<AssistantRuntimeConfig> ResolveAsync(
        string? overrideProvider = null,
        string? overrideModel = null,
        CancellationToken cancellationToken = default)
    {
        var options = optionsAccessor.Value;
        var dbDefaults = await assistantSettings.GetAsync(cancellationToken);

        var providerCandidate = FirstNonBlank(overrideProvider, dbDefaults?.Provider, options.Provider, LlmProviderKeys.Anthropic);
        var provider = LlmProviderKeys.Canonicalize(providerCandidate.Trim());

        var settings = await providerSettings.GetAsync(provider, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Assistant provider '{provider}' is not configured. Add a row in the LLM providers admin first.");

        if (!settings.HasApiKey)
        {
            throw new InvalidOperationException(
                $"Assistant provider '{provider}' has no API key configured.");
        }

        var modelCandidate = FirstNonBlank(overrideModel, dbDefaults?.Model, options.Model);
        var model = !string.IsNullOrWhiteSpace(modelCandidate)
            ? modelCandidate!.Trim()
            : settings.Models.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"Assistant provider '{provider}' has no models listed and no default model is set.");

        // Conversation-level cap: DB admin defaults win over options. Zero/null means uncapped.
        var maxPerConversation = dbDefaults?.MaxTokensPerConversation;
        if (maxPerConversation is { } v && v <= 0)
        {
            maxPerConversation = null;
        }

        return new AssistantRuntimeConfig(
            Provider: provider,
            Model: model,
            MaxTokens: options.MaxTokens > 0 ? options.MaxTokens : 4096,
            MaxTurns: options.MaxTurns > 0 ? options.MaxTurns : 10,
            MaxTokensPerConversation: maxPerConversation,
            AssignedAgentRoleId: dbDefaults?.AssignedAgentRoleId);
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }
        return string.Empty;
    }
}
