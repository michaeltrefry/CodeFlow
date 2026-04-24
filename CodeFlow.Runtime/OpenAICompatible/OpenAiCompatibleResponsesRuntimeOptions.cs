namespace CodeFlow.Runtime.OpenAICompatible;

public sealed record OpenAiCompatibleResponsesRuntimeOptions(
    Uri ResponsesEndpoint,
    string? ApiKey,
    int MaxRetryAttempts,
    TimeSpan InitialRetryDelay);
