namespace CodeFlow.Api.WorkflowPackages;

public sealed class WorkflowPackageResolutionException : Exception
{
    public WorkflowPackageResolutionException(string message)
        : base(message)
    {
    }

    public WorkflowPackageResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
