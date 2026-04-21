namespace CodeFlow.Runtime.Workspace;

public sealed record GitHostSettings(
    GitHostMode Mode,
    string? BaseUrl,
    bool HasToken,
    DateTime? LastVerifiedAtUtc,
    string? UpdatedBy,
    DateTime UpdatedAtUtc);

public sealed record GitHostSettingsWrite(
    GitHostMode Mode,
    string? BaseUrl,
    string Token,
    string? UpdatedBy);
