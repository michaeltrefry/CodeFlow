namespace CodeFlow.Persistence;

public sealed record WebSearchProviderSettings(
    string Provider,
    bool HasApiKey,
    string? EndpointUrl,
    string? UpdatedBy,
    DateTime UpdatedAtUtc);

public enum WebSearchProviderTokenAction
{
    Preserve = 0,
    Replace = 1,
    Clear = 2,
}

public sealed record WebSearchProviderTokenUpdate(WebSearchProviderTokenAction Action, string? Value)
{
    public static WebSearchProviderTokenUpdate Preserve() => new(WebSearchProviderTokenAction.Preserve, null);
    public static WebSearchProviderTokenUpdate Replace(string value) => new(WebSearchProviderTokenAction.Replace, value);
    public static WebSearchProviderTokenUpdate Clear() => new(WebSearchProviderTokenAction.Clear, null);
}

public sealed record WebSearchProviderSettingsWrite(
    string Provider,
    string? EndpointUrl,
    WebSearchProviderTokenUpdate Token,
    string? UpdatedBy);
