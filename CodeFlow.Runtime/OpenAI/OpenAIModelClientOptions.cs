namespace CodeFlow.Runtime.OpenAI;

public sealed record OpenAIModelClientOptions
{
    public string ApiKey { get; init; } = string.Empty;

    public Uri ResponsesEndpoint { get; init; } = new("https://api.openai.com/v1/responses");

    public int MaxRetryAttempts { get; init; } = 3;

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}
