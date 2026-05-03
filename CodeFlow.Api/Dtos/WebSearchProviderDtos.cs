namespace CodeFlow.Api.Dtos;

public sealed record WebSearchProviderResponse(
    string Provider,
    bool HasApiKey,
    string? EndpointUrl,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record WebSearchProviderWriteRequest(
    string Provider,
    string? EndpointUrl,
    WebSearchProviderTokenUpdateRequest? Token);

public enum WebSearchProviderTokenActionRequest
{
    Preserve = 0,
    Replace = 1,
    Clear = 2,
}

public sealed record WebSearchProviderTokenUpdateRequest(
    WebSearchProviderTokenActionRequest Action,
    string? Value);
