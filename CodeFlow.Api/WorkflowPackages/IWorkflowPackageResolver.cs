namespace CodeFlow.Api.WorkflowPackages;

public interface IWorkflowPackageResolver
{
    Task<WorkflowPackage> ResolveAsync(
        string workflowKey,
        int workflowVersion,
        CancellationToken cancellationToken = default);
}
