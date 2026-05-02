using CodeFlow.Api.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Endpoints;

/// <summary>
/// Anonymous discovery endpoint that lets first-run CodeFlow-CLI clients learn the OAuth
/// settings they need (authority, audience, public client id, scopes) from only the
/// CodeFlow API base URL — before they have a token to call <c>/api/me</c>.
/// </summary>
public static class AuthDiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapAuthDiscoveryEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/api/auth/config", (IOptionsSnapshot<AuthOptions> authOptions) =>
        {
            var options = authOptions.Value;

            // When real auth is enabled the API requires both Authority and Audience to be set
            // (AddCodeFlowAuth fails fast at startup). Re-check here so a config reload that
            // clears them surfaces as a structured 503 instead of an empty discovery payload
            // that would silently break CLI bootstrap.
            if (!options.DevelopmentBypass)
            {
                var missing = new List<string>(2);
                if (string.IsNullOrWhiteSpace(options.Authority))
                {
                    missing.Add(nameof(AuthOptions.Authority));
                }
                if (string.IsNullOrWhiteSpace(options.Audience))
                {
                    missing.Add(nameof(AuthOptions.Audience));
                }
                if (missing.Count > 0)
                {
                    return ApiResults.Error(
                        $"Auth discovery is unavailable: required configuration is missing ({string.Join(", ", missing)}).",
                        StatusCodes.Status503ServiceUnavailable);
                }
            }

            return Results.Ok(new AuthDiscoveryResponse(
                Authority: options.Authority ?? string.Empty,
                ClientId: options.CliClientId,
                Scopes: options.CliScopes,
                Audience: options.Audience ?? string.Empty));
        })
        .AllowAnonymous();

        return routes;
    }

    public sealed record AuthDiscoveryResponse(
        string Authority,
        string ClientId,
        string Scopes,
        string Audience);
}
