using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

/// <summary>
/// F2: HTTP surface over <see cref="IWorkflowDataflowAnalyzer"/>. Returns the per-node scope
/// snapshot the editor uses to render the data-flow inspector (VZ1), validate workflow-var
/// declarations (VZ2), and seed the script .d.ts narrowing (E1).
/// </summary>
public static class WorkflowDataflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowDataflowEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/workflows/{key}/{version:int}/dataflow");

        group.MapGet("/", GetSnapshotAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        return routes;
    }

    private static async Task<IResult> GetSnapshotAsync(
        string key,
        int version,
        IWorkflowRepository workflowRepository,
        IWorkflowDataflowAnalyzer analyzer,
        CancellationToken cancellationToken)
    {
        var workflow = await workflowRepository.TryGetAsync(key, version, cancellationToken);
        if (workflow is null)
        {
            return Results.NotFound();
        }

        var snapshot = analyzer.Analyze(workflow);
        return Results.Ok(snapshot);
    }
}
