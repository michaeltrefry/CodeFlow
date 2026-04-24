namespace CodeFlow.Api.Dtos;

public sealed record LlmProviderResponse(
    string Provider,
    bool HasApiKey,
    string? EndpointUrl,
    string? ApiVersion,
    IReadOnlyList<string> Models,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record LlmProviderWriteRequest(
    string? EndpointUrl,
    string? ApiVersion,
    IReadOnlyList<string>? Models,
    LlmProviderTokenUpdateRequest? Token);

public enum LlmProviderTokenActionRequest
{
    Preserve = 0,
    Replace = 1,
    Clear = 2,
}

public sealed record LlmProviderTokenUpdateRequest(
    LlmProviderTokenActionRequest Action,
    string? Value);

public sealed record LlmProviderModelOption(string Provider, string Model);
