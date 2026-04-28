namespace CodeFlow.Api.Assistant;

public sealed record AssistantRuntimeConfig(
    string Provider,
    string Model,
    int MaxTokens,
    int MaxTurns);
