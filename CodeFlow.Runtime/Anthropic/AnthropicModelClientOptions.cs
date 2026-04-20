namespace CodeFlow.Runtime.Anthropic;

public sealed record AnthropicModelClientOptions
{
    public string ApiKey { get; init; } = string.Empty;

    public Uri MessagesEndpoint { get; init; } = new("https://api.anthropic.com/v1/messages");

    public string ApiVersion { get; init; } = "2023-06-01";

    public int MaxRetryAttempts { get; init; } = 3;

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}
