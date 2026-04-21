namespace CodeFlow.Runtime.Workspace;

public interface IGitHostTokenProvider
{
    Task<GitHostTokenLease> AcquireAsync(CancellationToken cancellationToken = default);
}

public sealed class GitHostTokenLease : IDisposable
{
    private string? token;

    public GitHostTokenLease(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        this.token = token;
    }

    public string Token => token
        ?? throw new ObjectDisposedException(nameof(GitHostTokenLease));

    public void Dispose()
    {
        token = null;
    }
}
