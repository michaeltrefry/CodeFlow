using CodeFlow.Runtime.OpenAICompatible;

namespace CodeFlow.Runtime.LMStudio;

public sealed class LMStudioModelClient : OpenAiCompatibleResponsesModelClientBase
{
    public LMStudioModelClient(HttpClient httpClient, Func<LMStudioModelClientOptions> optionsResolver)
        : base(httpClient, ToRuntimeResolver(optionsResolver))
    {
    }

    // Convenience ctor for tests and for fixed-options call sites.
    public LMStudioModelClient(HttpClient httpClient, LMStudioModelClientOptions options)
        : this(httpClient, () => options ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    private static Func<OpenAiCompatibleResponsesRuntimeOptions> ToRuntimeResolver(
        Func<LMStudioModelClientOptions> optionsResolver)
    {
        ArgumentNullException.ThrowIfNull(optionsResolver);
        return () =>
        {
            var options = optionsResolver()
                ?? throw new InvalidOperationException("LMStudioModelClientOptions resolver returned null.");
            return new OpenAiCompatibleResponsesRuntimeOptions(
                options.ResponsesEndpoint,
                options.ApiKey,
                options.MaxRetryAttempts,
                options.InitialRetryDelay);
        };
    }
}
