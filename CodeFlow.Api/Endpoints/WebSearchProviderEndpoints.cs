using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Host.Web;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

public static class WebSearchProviderEndpoints
{
    public static IEndpointRouteBuilder MapWebSearchProviderEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var adminGroup = routes.MapGroup("/api/admin/web-search-provider");

        adminGroup.MapGet("/", GetAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WebSearchProviderRead);

        adminGroup.MapPut("/", PutAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WebSearchProviderWrite);

        return routes;
    }

    private static async Task<IResult> GetAsync(
        IWebSearchProviderSettingsRepository repository,
        CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(cancellationToken);
        if (settings is null)
        {
            return Results.Ok(new WebSearchProviderResponse(
                Provider: WebSearchProviderKeys.None,
                HasApiKey: false,
                EndpointUrl: null,
                UpdatedBy: null,
                UpdatedAtUtc: null));
        }

        return Results.Ok(new WebSearchProviderResponse(
            Provider: settings.Provider,
            HasApiKey: settings.HasApiKey,
            EndpointUrl: settings.EndpointUrl,
            UpdatedBy: settings.UpdatedBy,
            UpdatedAtUtc: settings.UpdatedAtUtc));
    }

    private static async Task<IResult> PutAsync(
        WebSearchProviderWriteRequest request,
        IWebSearchProviderSettingsRepository repository,
        IWebSearchProviderInvalidator invalidator,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("Request body is required.");
        }

        if (!WebSearchProviderKeys.IsKnown(request.Provider))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["provider"] = new[]
                {
                    $"Unknown provider '{request.Provider}'. Allowed: "
                    + string.Join(", ", WebSearchProviderKeys.All) + ".",
                },
            });
        }

        var canonical = WebSearchProviderKeys.Canonicalize(request.Provider);

        var errors = Validate(request, canonical);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var tokenUpdate = (request.Token?.Action ?? WebSearchProviderTokenActionRequest.Preserve) switch
        {
            WebSearchProviderTokenActionRequest.Replace when !string.IsNullOrWhiteSpace(request.Token?.Value) =>
                WebSearchProviderTokenUpdate.Replace(request.Token!.Value!),
            WebSearchProviderTokenActionRequest.Clear => WebSearchProviderTokenUpdate.Clear(),
            _ => WebSearchProviderTokenUpdate.Preserve(),
        };

        try
        {
            await repository.SetAsync(new WebSearchProviderSettingsWrite(
                Provider: canonical,
                EndpointUrl: request.EndpointUrl,
                Token: tokenUpdate,
                UpdatedBy: currentUser.Id), cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["token"] = new[] { ex.Message },
            });
        }

        invalidator.Invalidate();

        var refreshed = await repository.GetAsync(cancellationToken);
        return Results.Ok(refreshed is null
            ? new WebSearchProviderResponse(
                Provider: canonical,
                HasApiKey: false,
                EndpointUrl: null,
                UpdatedBy: null,
                UpdatedAtUtc: null)
            : new WebSearchProviderResponse(
                Provider: refreshed.Provider,
                HasApiKey: refreshed.HasApiKey,
                EndpointUrl: refreshed.EndpointUrl,
                UpdatedBy: refreshed.UpdatedBy,
                UpdatedAtUtc: refreshed.UpdatedAtUtc));
    }

    private static Dictionary<string, string[]> Validate(WebSearchProviderWriteRequest request, string provider)
    {
        var errors = new Dictionary<string, string[]>();

        var action = request.Token?.Action ?? WebSearchProviderTokenActionRequest.Preserve;
        if (action == WebSearchProviderTokenActionRequest.Replace
            && string.IsNullOrWhiteSpace(request.Token?.Value))
        {
            errors["token.value"] = new[] { "Token value is required when action is Replace." };
        }

        if (!string.IsNullOrWhiteSpace(request.EndpointUrl)
            && (!Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            errors["endpointUrl"] = new[] { "Endpoint URL must be an absolute http(s) URL." };
        }

        // Selecting "none" wipes the active provider; the API key is preserved unless the
        // operator explicitly clears it. This keeps a "switch off temporarily" flow clean
        // without forcing a re-paste of the secret on the next switch back.
        _ = provider;

        return errors;
    }
}
