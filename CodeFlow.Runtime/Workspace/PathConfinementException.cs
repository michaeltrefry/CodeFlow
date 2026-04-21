namespace CodeFlow.Runtime.Workspace;

public sealed class PathConfinementException : Exception
{
    public PathConfinementException(string message) : base(message)
    {
    }

    public PathConfinementException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
