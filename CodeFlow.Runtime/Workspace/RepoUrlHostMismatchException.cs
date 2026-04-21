namespace CodeFlow.Runtime.Workspace;

public sealed class RepoUrlHostMismatchException : Exception
{
    public RepoUrlHostMismatchException(string message) : base(message)
    {
    }
}
