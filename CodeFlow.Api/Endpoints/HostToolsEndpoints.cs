using CodeFlow.Api.Dtos;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

public static class HostToolsEndpoints
{
    public static IEndpointRouteBuilder MapHostToolsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/api/host-tools", ListAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentRolesRead);

        return routes;
    }

    private static IResult ListAsync()
    {
        var catalog = HostToolProvider.GetCatalog()
            .Concat(WorkspaceToolProvider.GetCatalog());
        var response = catalog
            .Select(tool => new HostToolResponse(
                Name: tool.Name,
                Description: tool.Description,
                Parameters: tool.Parameters?.DeepClone(),
                IsMutating: tool.IsMutating))
            .ToArray();

        return Results.Ok(response);
    }
}
