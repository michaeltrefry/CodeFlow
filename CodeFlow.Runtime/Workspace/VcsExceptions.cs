namespace CodeFlow.Runtime.Workspace;

public class VcsException : Exception
{
    protected VcsException(string message) : base(message) { }
    protected VcsException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class VcsUnauthorizedException : VcsException
{
    public VcsUnauthorizedException(string message) : base(message) { }
    public VcsUnauthorizedException(string message, Exception inner) : base(message, inner) { }
}

public sealed class VcsRepoNotFoundException : VcsException
{
    public VcsRepoNotFoundException(string owner, string name)
        : base($"Repository '{owner}/{name}' was not found.")
    {
        Owner = owner;
        Name = name;
    }

    public string Owner { get; }
    public string Name { get; }
}

public sealed class VcsConflictException : VcsException
{
    public VcsConflictException(string message) : base(message) { }
    public VcsConflictException(string message, Exception inner) : base(message, inner) { }
}

public sealed class VcsRateLimitedException : VcsException
{
    public VcsRateLimitedException(string message) : base(message) { }
    public VcsRateLimitedException(string message, Exception inner) : base(message, inner) { }
}
