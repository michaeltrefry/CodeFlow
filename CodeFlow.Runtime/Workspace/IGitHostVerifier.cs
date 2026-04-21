namespace CodeFlow.Runtime.Workspace;

public interface IGitHostVerifier
{
    Task<GitHostVerificationResult> VerifyAsync(
        GitHostMode mode,
        string? baseUrl,
        string token,
        CancellationToken cancellationToken = default);
}

public sealed record GitHostVerificationResult(bool Success, string? Error);
