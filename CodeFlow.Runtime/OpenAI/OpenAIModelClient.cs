using CodeFlow.Runtime.OpenAICompatible;

namespace CodeFlow.Runtime.OpenAI;

public sealed class OpenAIModelClient : OpenAiCompatibleResponsesModelClientBase
{
    public OpenAIModelClient(HttpClient httpClient, Func<OpenAIModelClientOptions> optionsResolver)
        : base(httpClient, ToRuntimeResolver(optionsResolver))
    {
    }

    // Convenience ctor for tests and for fixed-options call sites.
    public OpenAIModelClient(HttpClient httpClient, OpenAIModelClientOptions options)
        : this(httpClient, () => options ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    protected override void EnsureUsable(OpenAiCompatibleResponsesRuntimeOptions runtimeOptions)
    {
        if (string.IsNullOrWhiteSpace(runtimeOptions.ApiKey))
        {
            throw new InvalidOperationException(
                "An OpenAI API key has not been configured. Set it via the LLM providers settings page or the "
                + "'OpenAI:ApiKey' configuration value before invoking an OpenAI agent.");
        }
    }

    private static Func<OpenAiCompatibleResponsesRuntimeOptions> ToRuntimeResolver(
        Func<OpenAIModelClientOptions> optionsResolver)
    {
        ArgumentNullException.ThrowIfNull(optionsResolver);
        return () =>
        {
            var options = optionsResolver()
                ?? throw new InvalidOperationException("OpenAIModelClientOptions resolver returned null.");
            return new OpenAiCompatibleResponsesRuntimeOptions(
                options.ResponsesEndpoint,
                options.ApiKey,
                options.MaxRetryAttempts,
                options.InitialRetryDelay);
        };
    }
}
