namespace CodeFlow.Runtime;

public interface IModelClient
{
    Task<InvocationResponse> InvokeAsync(
        InvocationRequest request,
        CancellationToken cancellationToken = default);
}
