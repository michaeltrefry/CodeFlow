using CodeFlow.Api.Dtos;
using CodeFlow.Api.Auth;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

public static class GitHostEndpoints
{
    public static IEndpointRouteBuilder MapGitHostEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/admin/git-host");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.GitHostRead);

        group.MapPut("/", PutAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.GitHostWrite);

        group.MapPost("/verify", VerifyAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.GitHostWrite);

        return routes;
    }

    private static async Task<IResult> GetAsync(
        IGitHostSettingsRepository repository,
        CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(cancellationToken);
        if (settings is null)
        {
            return Results.Ok(new GitHostSettingsResponse(
                Mode: GitHostMode.GitHub,
                BaseUrl: null,
                HasToken: false,
                LastVerifiedAtUtc: null,
                UpdatedBy: null,
                UpdatedAtUtc: null));
        }

        return Results.Ok(new GitHostSettingsResponse(
            Mode: settings.Mode,
            BaseUrl: settings.BaseUrl,
            HasToken: settings.HasToken,
            LastVerifiedAtUtc: settings.LastVerifiedAtUtc,
            UpdatedBy: settings.UpdatedBy,
            UpdatedAtUtc: settings.UpdatedAtUtc));
    }

    private static async Task<IResult> PutAsync(
        GitHostSettingsRequest request,
        IGitHostSettingsRepository repository,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        await repository.SetAsync(new GitHostSettingsWrite(
            Mode: request.Mode,
            BaseUrl: request.BaseUrl,
            Token: request.Token!,
            UpdatedBy: currentUser.Id), cancellationToken);

        var updated = await repository.GetAsync(cancellationToken);
        return Results.Ok(new GitHostSettingsResponse(
            Mode: updated!.Mode,
            BaseUrl: updated.BaseUrl,
            HasToken: updated.HasToken,
            LastVerifiedAtUtc: updated.LastVerifiedAtUtc,
            UpdatedBy: updated.UpdatedBy,
            UpdatedAtUtc: updated.UpdatedAtUtc));
    }

    private static async Task<IResult> VerifyAsync(
        IGitHostSettingsRepository repository,
        IGitHostVerifier verifier,
        CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(cancellationToken);
        if (settings is null || !settings.HasToken)
        {
            return Results.BadRequest(new { error = "Git host settings are not configured." });
        }

        var token = await repository.GetDecryptedTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            return Results.BadRequest(new { error = "Git host token is not configured." });
        }

        var result = await verifier.VerifyAsync(settings.Mode, settings.BaseUrl, token, cancellationToken);
        if (!result.Success)
        {
            return Results.Ok(new GitHostVerifyResponse(
                Success: false,
                LastVerifiedAtUtc: settings.LastVerifiedAtUtc,
                Error: result.Error));
        }

        var verifiedAt = DateTime.UtcNow;
        await repository.MarkVerifiedAsync(verifiedAt, cancellationToken);

        return Results.Ok(new GitHostVerifyResponse(
            Success: true,
            LastVerifiedAtUtc: verifiedAt,
            Error: null));
    }

    private static Dictionary<string, string[]> Validate(GitHostSettingsRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["body"] = ["Request body is required."];
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            errors["token"] = ["Token is required."];
        }

        if (request.Mode == GitHostMode.GitLab)
        {
            if (string.IsNullOrWhiteSpace(request.BaseUrl))
            {
                errors["baseUrl"] = ["Base URL is required for GitLab mode."];
            }
            else if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri) ||
                     (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors["baseUrl"] = ["Base URL must be an absolute http(s) URL."];
            }
        }

        return errors;
    }
}
