using CodeFlow.Persistence;

namespace CodeFlow.Api.Assistant;

internal static class LlmProviderAuthentication
{
    internal const string LocalLmStudioApiKeyPlaceholder = "lmstudio-local";

    internal static bool RequiresApiKey(string provider) =>
        !string.Equals(provider, LlmProviderKeys.LmStudio, StringComparison.OrdinalIgnoreCase);

    internal static string FallbackApiKey(string provider) =>
        string.Equals(provider, LlmProviderKeys.LmStudio, StringComparison.OrdinalIgnoreCase)
            ? LocalLmStudioApiKeyPlaceholder
            : string.Empty;
}
