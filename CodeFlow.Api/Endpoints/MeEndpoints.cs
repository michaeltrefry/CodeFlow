using CodeFlow.Api.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/me")
            .RequireAuthorization(CodeFlowApiDefaults.Policies.Authenticated);

        group.MapGet("/", (ICurrentUser currentUser) =>
        {
            return Results.Ok(new CurrentUserResponse(
                Id: currentUser.Id ?? string.Empty,
                Email: currentUser.Email,
                Name: currentUser.Name,
                Roles: currentUser.Roles));
        });

        return routes;
    }

    public sealed record CurrentUserResponse(
        string Id,
        string? Email,
        string? Name,
        IReadOnlyList<string> Roles);
}
