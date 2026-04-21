namespace CodeFlow.Runtime.Workspace;

public interface IVcsProviderFactory
{
    Task<IVcsProvider> CreateAsync(CancellationToken cancellationToken = default);
}

public sealed class GitHostNotConfiguredException : Exception
{
    public GitHostNotConfiguredException() : base("Git host settings have not been configured.") { }
}
