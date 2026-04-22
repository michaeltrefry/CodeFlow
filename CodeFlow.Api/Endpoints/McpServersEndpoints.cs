using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.Mcp;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.Endpoints;

public static class McpServersEndpoints
{
    public static IEndpointRouteBuilder MapMcpServersEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/mcp-servers");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.McpServersRead);

        group.MapGet("/{id:long}", GetAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.McpServersRead);

        group.MapGet("/{id:long}/tools", GetToolsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.McpServersRead);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.McpServersWrite);

        group.MapPut("/{id:long}", UpdateAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.McpServersWrite);

        group.MapDelete("/{id:long}", ArchiveAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.McpServersWrite);

        group.MapPost("/{id:long}/verify", VerifyAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.McpServersWrite);

        group.MapPost("/{id:long}/refresh-tools", RefreshToolsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.McpServersWrite);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        IMcpServerRepository repository,
        bool? includeArchived,
        CancellationToken cancellationToken)
    {
        var servers = await repository.ListAsync(includeArchived ?? false, cancellationToken);
        return Results.Ok(servers.Select(Map).ToArray());
    }

    private static async Task<IResult> GetAsync(
        long id,
        IMcpServerRepository repository,
        CancellationToken cancellationToken)
    {
        var server = await repository.GetAsync(id, cancellationToken);
        return server is null ? Results.NotFound() : Results.Ok(Map(server));
    }

    private static async Task<IResult> GetToolsAsync(
        long id,
        IMcpServerRepository repository,
        CancellationToken cancellationToken)
    {
        var server = await repository.GetAsync(id, cancellationToken);
        if (server is null)
        {
            return Results.NotFound();
        }

        var tools = await repository.GetToolsAsync(id, cancellationToken);
        return Results.Ok(tools.Select(MapTool).ToArray());
    }

    private static async Task<IResult> CreateAsync(
        McpServerCreateRequest request,
        IMcpServerRepository repository,
        IMcpEndpointPolicy endpointPolicy,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var errors = ValidateCreate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var policyErrors = await ValidateEndpointAsync(request.EndpointUrl!, endpointPolicy, cancellationToken);
        if (policyErrors.Count > 0)
        {
            return Results.ValidationProblem(policyErrors);
        }

        var existing = await repository.GetByKeyAsync(request.Key!, cancellationToken);
        if (existing is not null)
        {
            return Results.Conflict(new { error = $"MCP server with key '{request.Key}' already exists." });
        }

        var id = await repository.CreateAsync(new McpServerCreate(
            Key: request.Key!,
            DisplayName: request.DisplayName!,
            Transport: request.Transport,
            EndpointUrl: request.EndpointUrl!,
            BearerTokenPlaintext: string.IsNullOrEmpty(request.BearerToken) ? null : request.BearerToken,
            CreatedBy: currentUser.Id), cancellationToken);

        var created = await repository.GetAsync(id, cancellationToken);
        return Results.Created($"/api/mcp-servers/{id}", Map(created!));
    }

    private static async Task<IResult> UpdateAsync(
        long id,
        McpServerUpdateRequest request,
        IMcpServerRepository repository,
        IMcpEndpointPolicy endpointPolicy,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var errors = ValidateUpdate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var policyErrors = await ValidateEndpointAsync(request.EndpointUrl!, endpointPolicy, cancellationToken);
        if (policyErrors.Count > 0)
        {
            return Results.ValidationProblem(policyErrors);
        }

        var bearerUpdate = (request.BearerToken?.Action ?? BearerTokenAction.Preserve) switch
        {
            BearerTokenAction.Preserve => BearerTokenUpdate.Preserve(),
            BearerTokenAction.Clear => BearerTokenUpdate.Clear(),
            BearerTokenAction.Replace when !string.IsNullOrEmpty(request.BearerToken?.Value) =>
                BearerTokenUpdate.Replace(request.BearerToken!.Value!),
            _ => BearerTokenUpdate.Preserve(),
        };

        try
        {
            await repository.UpdateAsync(id, new McpServerUpdate(
                DisplayName: request.DisplayName!,
                Transport: request.Transport,
                EndpointUrl: request.EndpointUrl!,
                BearerToken: bearerUpdate,
                UpdatedBy: currentUser.Id), cancellationToken);
        }
        catch (McpServerNotFoundException)
        {
            return Results.NotFound();
        }

        var updated = await repository.GetAsync(id, cancellationToken);
        return Results.Ok(Map(updated!));
    }

    private static async Task<IResult> ArchiveAsync(
        long id,
        IMcpServerRepository repository,
        CancellationToken cancellationToken)
    {
        try
        {
            await repository.ArchiveAsync(id, cancellationToken);
        }
        catch (McpServerNotFoundException)
        {
            return Results.NotFound();
        }

        return Results.NoContent();
    }

    private static async Task<IResult> VerifyAsync(
        long id,
        IMcpServerRepository repository,
        McpToolDiscovery discovery,
        CancellationToken cancellationToken)
    {
        var server = await repository.GetAsync(id, cancellationToken);
        if (server is null)
        {
            return Results.NotFound();
        }

        var info = await repository.GetConnectionInfoAsync(server.Key, cancellationToken);
        if (info is null)
        {
            return Results.NotFound();
        }

        var result = await discovery.DiscoverAsync(info, cancellationToken);
        var now = DateTime.UtcNow;

        if (result is McpDiscoverySuccess success)
        {
            await repository.UpdateHealthAsync(id, McpServerHealthStatus.Healthy, now, null, cancellationToken);
            return Results.Ok(new McpServerVerifyResponse(
                HealthStatus: McpServerHealthStatus.Healthy,
                LastVerifiedAtUtc: now,
                LastVerificationError: null,
                DiscoveredToolCount: success.Tools.Count));
        }

        var failure = (McpDiscoveryFailure)result;
        await repository.UpdateHealthAsync(id, McpServerHealthStatus.Unhealthy, now, failure.Message, cancellationToken);
        return Results.Ok(new McpServerVerifyResponse(
            HealthStatus: McpServerHealthStatus.Unhealthy,
            LastVerifiedAtUtc: now,
            LastVerificationError: failure.Message,
            DiscoveredToolCount: null));
    }

    private static async Task<IResult> RefreshToolsAsync(
        long id,
        IMcpServerRepository repository,
        McpToolDiscovery discovery,
        CancellationToken cancellationToken)
    {
        var server = await repository.GetAsync(id, cancellationToken);
        if (server is null)
        {
            return Results.NotFound();
        }

        var info = await repository.GetConnectionInfoAsync(server.Key, cancellationToken);
        if (info is null)
        {
            return Results.NotFound();
        }

        var result = await discovery.DiscoverAsync(info, cancellationToken);
        var now = DateTime.UtcNow;

        if (result is McpDiscoverySuccess success)
        {
            var writes = success.Tools
                .Select(tool => new McpServerToolWrite(
                    ToolName: tool.ToolName,
                    Description: tool.Description,
                    ParametersJson: tool.Parameters?.ToJsonString(),
                    IsMutating: tool.IsMutating))
                .ToArray();

            await repository.ReplaceToolsAsync(id, writes, cancellationToken);
            await repository.UpdateHealthAsync(id, McpServerHealthStatus.Healthy, now, null, cancellationToken);

            var persisted = await repository.GetToolsAsync(id, cancellationToken);
            return Results.Ok(new McpServerRefreshResponse(
                HealthStatus: McpServerHealthStatus.Healthy,
                LastVerifiedAtUtc: now,
                LastVerificationError: null,
                Tools: persisted.Select(MapTool).ToArray()));
        }

        var failure = (McpDiscoveryFailure)result;
        await repository.UpdateHealthAsync(id, McpServerHealthStatus.Unhealthy, now, failure.Message, cancellationToken);
        return Results.Ok(new McpServerRefreshResponse(
            HealthStatus: McpServerHealthStatus.Unhealthy,
            LastVerifiedAtUtc: now,
            LastVerificationError: failure.Message,
            Tools: Array.Empty<McpServerToolResponse>()));
    }

    private static async Task<Dictionary<string, string[]>> ValidateEndpointAsync(
        string endpointUrl,
        IMcpEndpointPolicy policy,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri))
        {
            // Shape validation already covers this; defensive here so the policy never runs against null.
            return errors;
        }

        var result = await policy.ValidateAsync(uri, cancellationToken);
        if (!result.IsAllowed)
        {
            errors["endpointUrl"] = new[]
            {
                result.Reason ?? "Endpoint is not allowed by the configured MCP endpoint policy.",
            };
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateCreate(McpServerCreateRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["body"] = ["Request body is required."];
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            errors["key"] = ["Key is required."];
        }
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors["displayName"] = ["Display name is required."];
        }
        if (string.IsNullOrWhiteSpace(request.EndpointUrl) || !Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out _))
        {
            errors["endpointUrl"] = ["Endpoint URL must be an absolute URI."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateUpdate(McpServerUpdateRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["body"] = ["Request body is required."];
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors["displayName"] = ["Display name is required."];
        }
        if (string.IsNullOrWhiteSpace(request.EndpointUrl) || !Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out _))
        {
            errors["endpointUrl"] = ["Endpoint URL must be an absolute URI."];
        }

        if (request.BearerToken is { Action: BearerTokenAction.Replace } replace && string.IsNullOrEmpty(replace.Value))
        {
            errors["bearerToken.value"] = ["Value is required when action is Replace."];
        }

        return errors;
    }

    private static McpServerResponse Map(McpServer server) => new(
        Id: server.Id,
        Key: server.Key,
        DisplayName: server.DisplayName,
        Transport: server.Transport,
        EndpointUrl: server.EndpointUrl,
        HasBearerToken: server.HasBearerToken,
        HealthStatus: server.HealthStatus,
        LastVerifiedAtUtc: server.LastVerifiedAtUtc,
        LastVerificationError: server.LastVerificationError,
        CreatedAtUtc: server.CreatedAtUtc,
        CreatedBy: server.CreatedBy,
        UpdatedAtUtc: server.UpdatedAtUtc,
        UpdatedBy: server.UpdatedBy,
        IsArchived: server.IsArchived);

    private static McpServerToolResponse MapTool(McpServerTool tool) => new(
        Id: tool.Id,
        ServerId: tool.ServerId,
        ToolName: tool.ToolName,
        Description: tool.Description,
        Parameters: string.IsNullOrEmpty(tool.ParametersJson) ? null : TryParseNode(tool.ParametersJson),
        IsMutating: tool.IsMutating,
        SyncedAtUtc: tool.SyncedAtUtc);

    private static JsonNode? TryParseNode(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }
}
