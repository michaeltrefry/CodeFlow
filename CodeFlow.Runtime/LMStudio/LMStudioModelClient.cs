using CodeFlow.Runtime.OpenAICompatible;

namespace CodeFlow.Runtime.LMStudio;

public sealed class LMStudioModelClient : OpenAiCompatibleResponsesModelClientBase
{
    public LMStudioModelClient(HttpClient httpClient, LMStudioModelClientOptions options)
        : base(
            httpClient,
            options?.ResponsesEndpoint ?? throw new ArgumentNullException(nameof(options)),
            options.ApiKey,
            options.MaxRetryAttempts,
            options.InitialRetryDelay)
    {
    }
}
