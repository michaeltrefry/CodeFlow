using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

public static class CodeFlowEndpoints
{
    public static IEndpointRouteBuilder MapCodeFlowEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapMeEndpoints();
        routes.MapAgentsEndpoints();
        routes.MapAgentTestEndpoints();
        routes.MapWorkflowsEndpoints();
        routes.MapTracesEndpoints();
        routes.MapOpsEndpoints();
        routes.MapMcpServersEndpoints();
        routes.MapHostToolsEndpoints();
        routes.MapAgentRolesEndpoints();
        routes.MapSkillsEndpoints();
        routes.MapGitHostEndpoints();
        routes.MapLlmProvidersEndpoints();
        routes.MapWorkflowTemplatesEndpoints();

        return routes;
    }
}
