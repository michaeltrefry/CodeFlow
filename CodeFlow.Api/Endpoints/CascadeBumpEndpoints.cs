using CodeFlow.Api.Auth;
using CodeFlow.Api.CascadeBump;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

/// <summary>
/// E4 (Workflow Authoring DX): HTTP surface for the cascade-bump assistant. Plan returns the
/// tree of workflows that would be bumped if a given agent or workflow's new version is rolled
/// forward through every pinning workflow. Apply executes that plan.
/// </summary>
public static class CascadeBumpEndpoints
{
    public static IEndpointRouteBuilder MapCascadeBumpEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/workflows/cascade-bump");

        group.MapPost("/plan", PlanAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapPost("/apply", ApplyAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        return routes;
    }

    private static async Task<IResult> PlanAsync(
        CascadeBumpRequest? request,
        CascadeBumpPlanner planner,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("Request body is required.");
        }

        try
        {
            var plan = await planner.PlanAsync(request, cancellationToken);
            return Results.Ok(plan);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [string.IsNullOrWhiteSpace(ex.ParamName) ? "request" : ex.ParamName!] = [ex.Message],
            });
        }
        catch (CascadeBumpRootNotFoundException ex)
        {
            return ApiResults.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> ApplyAsync(
        CascadeBumpRequest? request,
        CascadeBumpExecutor executor,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("Request body is required.");
        }

        try
        {
            var result = await executor.ApplyAsync(request, cancellationToken);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [string.IsNullOrWhiteSpace(ex.ParamName) ? "request" : ex.ParamName!] = [ex.Message],
            });
        }
        catch (CascadeBumpRootNotFoundException ex)
        {
            return ApiResults.NotFound(ex.Message);
        }
    }
}
