using CodeFlow.Runtime.OpenAICompatible;

namespace CodeFlow.Runtime.OpenAI;

public sealed class OpenAIModelClient : OpenAiCompatibleResponsesModelClientBase
{
    public OpenAIModelClient(HttpClient httpClient, OpenAIModelClientOptions options)
        : base(
            httpClient,
            options?.ResponsesEndpoint ?? throw new ArgumentNullException(nameof(options)),
            options.ApiKey,
            options.MaxRetryAttempts,
            options.InitialRetryDelay)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("An OpenAI API key is required.", nameof(options));
        }
    }
}
