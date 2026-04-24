using CodeFlow.Runtime.Anthropic;
using CodeFlow.Runtime.LMStudio;
using CodeFlow.Runtime.OpenAI;

namespace CodeFlow.Runtime;

/// <summary>
/// Resolves current (possibly runtime-updated) options for each built-in provider. The default
/// implementation merges DB-stored admin settings over the appsettings baseline. Model clients
/// invoke the resolver on every call so operators can rotate keys or change model lists without
/// restarting the host.
/// </summary>
public interface ILlmProviderConfigResolver
{
    OpenAIModelClientOptions ResolveOpenAI();

    AnthropicModelClientOptions ResolveAnthropic();

    LMStudioModelClientOptions ResolveLMStudio();

    IReadOnlyList<string> ResolveConfiguredModels(string provider);
}
