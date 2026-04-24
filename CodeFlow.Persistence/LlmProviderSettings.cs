namespace CodeFlow.Persistence;

public sealed record LlmProviderSettings(
    string Provider,
    bool HasApiKey,
    string? EndpointUrl,
    string? ApiVersion,
    IReadOnlyList<string> Models,
    string? UpdatedBy,
    DateTime UpdatedAtUtc);

public enum LlmProviderTokenAction
{
    Preserve = 0,
    Replace = 1,
    Clear = 2,
}

public sealed record LlmProviderTokenUpdate(LlmProviderTokenAction Action, string? Value)
{
    public static LlmProviderTokenUpdate Preserve() => new(LlmProviderTokenAction.Preserve, null);
    public static LlmProviderTokenUpdate Replace(string value) => new(LlmProviderTokenAction.Replace, value);
    public static LlmProviderTokenUpdate Clear() => new(LlmProviderTokenAction.Clear, null);
}

public sealed record LlmProviderSettingsWrite(
    string Provider,
    string? EndpointUrl,
    string? ApiVersion,
    IReadOnlyList<string>? Models,
    LlmProviderTokenUpdate Token,
    string? UpdatedBy);
