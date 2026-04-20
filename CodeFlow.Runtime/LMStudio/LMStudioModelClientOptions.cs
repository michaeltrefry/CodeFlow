namespace CodeFlow.Runtime.LMStudio;

public sealed record LMStudioModelClientOptions
{
    public string ApiKey { get; init; } = string.Empty;

    public Uri ResponsesEndpoint { get; init; } = new("http://localhost:1234/v1/responses");

    public int MaxRetryAttempts { get; init; } = 3;

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}
