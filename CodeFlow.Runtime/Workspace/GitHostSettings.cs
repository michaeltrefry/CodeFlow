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
    GitHostTokenUpdate Token,
    string? UpdatedBy);

/// <summary>
/// Explicit preserve/replace semantics for the git-host personal access token, mirroring the
/// MCP bearer-token update contract. Administrators editing non-secret settings (mode or base
/// URL) must be able to save without re-entering the token.
/// </summary>
public sealed record GitHostTokenUpdate(GitHostTokenAction Action, string? Value)
{
    public static GitHostTokenUpdate Preserve() => new(GitHostTokenAction.Preserve, null);

    public static GitHostTokenUpdate Replace(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new GitHostTokenUpdate(GitHostTokenAction.Replace, value);
    }
}

public enum GitHostTokenAction
{
    Preserve = 0,
    Replace = 1,
}
