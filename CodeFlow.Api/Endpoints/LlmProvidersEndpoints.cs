using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Host;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

public static class LlmProvidersEndpoints
{
    public static IEndpointRouteBuilder MapLlmProvidersEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var adminGroup = routes.MapGroup("/api/admin/llm-providers");

        adminGroup.MapGet("/", ListAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.LlmProvidersRead);

        adminGroup.MapPut("/{provider}", PutAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.LlmProvidersWrite);

        // Read-only list of available provider+model pairs — needed by the agent editor dropdown,
        // scoped to any authenticated user who can read agents.
        routes.MapGet("/api/llm-providers/models", ListModelsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsRead);

        // HAA-15: assistant defaults piggy-back on the LLM providers admin auth — same operators,
        // same surface, just the assistant-specific selection within the configured providers.
        var assistantGroup = routes.MapGroup("/api/admin/assistant-settings");
        assistantGroup.MapGet("/", GetAssistantSettingsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.LlmProvidersRead);
        assistantGroup.MapPut("/", PutAssistantSettingsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.LlmProvidersWrite);

        return routes;
    }

    private static async Task<IResult> GetAssistantSettingsAsync(
        IAssistantSettingsRepository repository,
        CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(cancellationToken);
        return Results.Ok(new AssistantSettingsResponse(
            Provider: settings?.Provider,
            Model: settings?.Model,
            MaxTokensPerConversation: settings?.MaxTokensPerConversation,
            AssignedAgentRoleId: settings?.AssignedAgentRoleId,
            Instructions: settings?.Instructions,
            UpdatedBy: settings?.UpdatedBy,
            UpdatedAtUtc: settings?.UpdatedAtUtc));
    }

    private static async Task<IResult> PutAssistantSettingsAsync(
        AssistantSettingsWriteRequest request,
        IAssistantSettingsRepository repository,
        IAgentRoleRepository agentRoleRepository,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("Request body is required.");
        }

        var errors = new Dictionary<string, string[]>();
        if (!string.IsNullOrWhiteSpace(request.Provider) && !LlmProviderKeys.IsKnown(request.Provider))
        {
            errors["provider"] = new[] { $"Unknown provider '{request.Provider}'." };
        }
        if (request.MaxTokensPerConversation is { } cap && cap < 0)
        {
            errors["maxTokensPerConversation"] = new[] { "Cap must be zero (uncapped) or positive." };
        }
        if (request.AssignedAgentRoleId is { } roleId)
        {
            if (roleId <= 0)
            {
                errors["assignedAgentRoleId"] = new[] { "Role id must be positive." };
            }
            else
            {
                var role = await agentRoleRepository.GetAsync(roleId, cancellationToken);
                if (role is null || role.IsArchived)
                {
                    errors["assignedAgentRoleId"] = new[] { $"Agent role {roleId} is not available." };
                }
            }
        }
        if (request.Instructions is { Length: > MaxInstructionsLength })
        {
            errors["instructions"] = new[] { $"Instructions must be {MaxInstructionsLength} characters or fewer." };
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var saved = await repository.SetAsync(new AssistantSettingsWrite(
            Provider: request.Provider,
            Model: request.Model,
            MaxTokensPerConversation: request.MaxTokensPerConversation,
            AssignedAgentRoleId: request.AssignedAgentRoleId,
            Instructions: request.Instructions,
            UpdatedBy: currentUser.Id), cancellationToken);

        return Results.Ok(new AssistantSettingsResponse(
            Provider: saved.Provider,
            Model: saved.Model,
            MaxTokensPerConversation: saved.MaxTokensPerConversation,
            AssignedAgentRoleId: saved.AssignedAgentRoleId,
            Instructions: saved.Instructions,
            UpdatedBy: saved.UpdatedBy,
            UpdatedAtUtc: saved.UpdatedAtUtc));
    }

    /// <summary>
    /// Hard cap on the operator-authored instructions overlay. Big enough for several paragraphs
    /// describing role-granted tools / scope rules; small enough to keep the system prompt under
    /// the model's effective context budget.
    /// </summary>
    private const int MaxInstructionsLength = 16_000;

    private static async Task<IResult> ListAsync(
        ILlmProviderSettingsRepository repository,
        CancellationToken cancellationToken)
    {
        var all = await repository.GetAllAsync(cancellationToken);
        var byKey = all.ToDictionary(s => s.Provider, StringComparer.OrdinalIgnoreCase);

        var responses = LlmProviderKeys.All
            .Select(provider =>
            {
                if (byKey.TryGetValue(provider, out var settings))
                {
                    return new LlmProviderResponse(
                        Provider: settings.Provider,
                        HasApiKey: settings.HasApiKey,
                        EndpointUrl: settings.EndpointUrl,
                        ApiVersion: settings.ApiVersion,
                        Models: settings.Models,
                        UpdatedBy: settings.UpdatedBy,
                        UpdatedAtUtc: settings.UpdatedAtUtc);
                }

                return new LlmProviderResponse(
                    Provider: provider,
                    HasApiKey: false,
                    EndpointUrl: null,
                    ApiVersion: null,
                    Models: Array.Empty<string>(),
                    UpdatedBy: null,
                    UpdatedAtUtc: null);
            })
            .ToArray();

        return Results.Ok(responses);
    }

    private static async Task<IResult> PutAsync(
        string provider,
        LlmProviderWriteRequest request,
        ILlmProviderSettingsRepository repository,
        ILlmProviderConfigInvalidator invalidator,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (!LlmProviderKeys.IsKnown(provider))
        {
            return ApiResults.NotFound($"Unknown provider '{provider}'.");
        }

        var canonical = LlmProviderKeys.Canonicalize(provider);

        var errors = Validate(request, canonical);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var tokenUpdate = (request.Token?.Action ?? LlmProviderTokenActionRequest.Preserve) switch
        {
            LlmProviderTokenActionRequest.Replace when !string.IsNullOrWhiteSpace(request.Token?.Value) =>
                LlmProviderTokenUpdate.Replace(request.Token!.Value!),
            LlmProviderTokenActionRequest.Clear => LlmProviderTokenUpdate.Clear(),
            _ => LlmProviderTokenUpdate.Preserve(),
        };

        try
        {
            await repository.SetAsync(new LlmProviderSettingsWrite(
                Provider: canonical,
                EndpointUrl: request.EndpointUrl,
                ApiVersion: request.ApiVersion,
                Models: request.Models,
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

        invalidator.Invalidate(canonical);

        var refreshed = await repository.GetAsync(canonical, cancellationToken);
        if (refreshed is null)
        {
            // Write was a no-op (e.g. Preserve on a fresh row with no content) — return empty shape.
            return Results.Ok(new LlmProviderResponse(
                Provider: canonical,
                HasApiKey: false,
                EndpointUrl: null,
                ApiVersion: null,
                Models: Array.Empty<string>(),
                UpdatedBy: null,
                UpdatedAtUtc: null));
        }

        return Results.Ok(new LlmProviderResponse(
            Provider: refreshed.Provider,
            HasApiKey: refreshed.HasApiKey,
            EndpointUrl: refreshed.EndpointUrl,
            ApiVersion: refreshed.ApiVersion,
            Models: refreshed.Models,
            UpdatedBy: refreshed.UpdatedBy,
            UpdatedAtUtc: refreshed.UpdatedAtUtc));
    }

    private static async Task<IResult> ListModelsAsync(
        ILlmProviderSettingsRepository repository,
        CancellationToken cancellationToken)
    {
        var all = await repository.GetAllAsync(cancellationToken);
        var options = all
            .SelectMany(s => s.Models.Select(model => new LlmProviderModelOption(s.Provider, model)))
            .ToArray();
        return Results.Ok(options);
    }

    private static Dictionary<string, string[]> Validate(LlmProviderWriteRequest? request, string provider)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["body"] = ["Request body is required."];
            return errors;
        }

        var action = request.Token?.Action ?? LlmProviderTokenActionRequest.Preserve;
        if (action == LlmProviderTokenActionRequest.Replace && string.IsNullOrWhiteSpace(request.Token?.Value))
        {
            errors["token.value"] = ["Token value is required when action is Replace."];
        }

        if (!string.IsNullOrWhiteSpace(request.EndpointUrl)
            && (!Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            errors["endpointUrl"] = ["Endpoint URL must be an absolute http(s) URL."];
        }

        if (request.Models is not null)
        {
            foreach (var model in request.Models)
            {
                if (string.IsNullOrWhiteSpace(model))
                {
                    errors["models"] = ["Model ids must not be blank."];
                    break;
                }
            }
        }

        // api_version is only meaningful for Anthropic, but we accept it silently for other
        // providers so the UI can send a common shape without provider-specific branches.

        return errors;
    }
}
